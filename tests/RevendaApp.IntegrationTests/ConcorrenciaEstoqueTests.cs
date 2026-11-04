using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.IntegrationTests.TestHelpers;
using RevendaApp.Web.Common;
using RevendaApp.Web.Common.Behaviors;
using RevendaApp.Web.Features.Clientes.Pagar;

namespace RevendaApp.IntegrationTests.Tests;

/// <summary>
/// Duas operações concorrentes sobre a mesma linha de <see cref="Produto"/> não podem as duas
/// passarem: a segunda precisa falhar com conflito em vez de gravar estoque negativo.
/// </summary>
public class ConcorrenciaEstoqueTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Segunda_gravacao_concorrente_do_mesmo_produto_falha_com_conflito()
    {
        Guid produtoId;
        await using (var preparo = fixture.CreateDbContext())
        {
            var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m);
            produto.RegistrarEntrada(1);
            preparo.Produtos.Add(produto);
            await preparo.SaveChangesAsync();
            produtoId = produto.Id;
        }

        // Dois contextos independentes = duas requisições simultâneas.
        await using var caixaA = fixture.CreateDbContext();
        await using var caixaB = fixture.CreateDbContext();

        var produtoA = await caixaA.Produtos.SingleAsync(p => p.Id == produtoId);
        var produtoB = await caixaB.Produtos.SingleAsync(p => p.Id == produtoId);

        produtoA.RegistrarSaida(1);
        await caixaA.SaveChangesAsync(); // vence a corrida

        produtoB.RegistrarSaida(1); // a validação em memória passa (leu 1)
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => caixaB.SaveChangesAsync());
    }

    [Fact]
    public async Task Segunda_gravacao_concorrente_do_mesmo_cliente_falha_com_conflito()
    {
        Guid clienteId;
        await using (var preparo = fixture.CreateDbContext())
        {
            var cliente = new Cliente("Maria Souza", "11988887777", "Rua A, 1");
            cliente.AdicionarSaldoDevedor(100m);
            preparo.Clientes.Add(cliente);
            await preparo.SaveChangesAsync();
            clienteId = cliente.Id;
        }

        await using var caixaA = fixture.CreateDbContext();
        await using var caixaB = fixture.CreateDbContext();

        var clienteA = await caixaA.Clientes.SingleAsync(c => c.Id == clienteId);
        var clienteB = await caixaB.Clientes.SingleAsync(c => c.Id == clienteId);

        clienteA.RegistrarPagamento(40m);
        await caixaA.SaveChangesAsync();

        clienteB.RegistrarPagamento(40m);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => caixaB.SaveChangesAsync());
    }

    /// <summary>
    /// Regressão do bug crítico: depois de um conflito traduzido pelo
    /// <see cref="ConcorrenciaBehavior{TMessage,TResponse}"/>, o change tracker precisa ficar vazio.
    /// Sem a limpeza, o <c>Pagamento</c> pendente da tentativa que falhou seria commitado pela
    /// próxima operação do mesmo circuito Blazor (que é scoped e não é recriado ao fechar o modal) —
    /// um pagamento fantasma que o operador acredita nunca ter acontecido.
    /// </summary>
    [Fact]
    public async Task Conflito_traduzido_nao_deixa_operacao_fantasma_para_o_proximo_save()
    {
        Guid clienteId;
        await using (var preparo = fixture.CreateDbContext())
        {
            var cliente = new Cliente("Maria Souza", "11988887777", "Rua A, 1");
            cliente.AdicionarSaldoDevedor(100m);
            preparo.Clientes.Add(cliente);
            await preparo.SaveChangesAsync();
            clienteId = cliente.Id;
        }

        await using var caixa = fixture.CreateDbContext();

        // O circuito já tinha o cliente carregado (é o que acontece na tela: o operador abriu o
        // detalhe antes). É essa leitura anterior que deixa o `xmin` defasado.
        _ = await caixa.Clientes.SingleAsync(c => c.Id == clienteId);

        // Outra requisição grava o mesmo cliente depois da nossa leitura.
        await using (var concorrente = fixture.CreateDbContext())
        {
            var clienteConcorrente = await concorrente.Clientes.SingleAsync(c => c.Id == clienteId);
            clienteConcorrente.RegistrarPagamento(10m);
            await concorrente.SaveChangesAsync();
        }

        var handler = new RegistrarPagamentoCommandHandler(caixa, TenantIntegracao.Contexto(), new VendasAtualizadasNotifier());
        var behavior = new ConcorrenciaBehavior<RegistrarPagamentoCommand, RegistrarPagamentoResult>(caixa);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await behavior.Handle(
                new RegistrarPagamentoCommand(clienteId, 40m, FormaPagamento.Dinheiro),
                (mensagem, ct) => handler.Handle((RegistrarPagamentoCommand)mensagem, ct),
                CancellationToken.None));

        Assert.Equal(Concorrencia.Mensagem, ex.Message);

        // O tracker tem que estar limpo — é o que impede o commit fantasma abaixo.
        Assert.Empty(caixa.ChangeTracker.Entries());

        // Uma operação seguinte no MESMO contexto (o que o operador faria ao fechar o modal) não
        // pode gravar o pagamento da tentativa que falhou.
        await caixa.SaveChangesAsync();

        await using var verificacao = fixture.CreateDbContext();
        var pagamentos = await verificacao.Pagamentos.Where(p => p.ClienteId == clienteId).ToListAsync();
        Assert.Empty(pagamentos);
        Assert.Equal(90m, (await verificacao.Clientes.SingleAsync(c => c.Id == clienteId)).SaldoDevedor);
    }
}