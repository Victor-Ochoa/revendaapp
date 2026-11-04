using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;

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
}