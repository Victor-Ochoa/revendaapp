using RevendaApp.Domain.Enums;
using RevendaApp.Infrastructure;
using RevendaApp.IntegrationTests.TestHelpers;
using RevendaApp.Web.Features.Admin.Vendas;
using RevendaApp.Web.Features.Clientes.Listar;
using RevendaApp.Web.Features.Estoque.Cadastrar;
using RevendaApp.Web.Features.Estoque.Listar;
using RevendaApp.Web.Features.Estoque.Movimentar;
using RevendaApp.Web.Features.Vendas.NovaVenda;

namespace RevendaApp.IntegrationTests.Tests;

/// <summary>
/// Testes de integração da gestão de vendas administrativa (Configurações → Vendas): listar
/// vendas não excluídas, editar re-recriando (recalcula saldo fiado e estoque) e excluir com
/// estorno — contra o Postgres real via <see cref="DatabaseFixture"/>.
/// </summary>
public class AdminVendasFeatureTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<Guid> CriarProdutoComEstoqueAsync(AppDbContext db, int estoque = 20)
    {
        var produtoId = await new CadastrarProdutoCommandHandler(db, TenantIntegracao.Contexto())
            .Handle(new CadastrarProdutoCommand("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m, 5), CancellationToken.None);
        await new RegistrarMovimentacaoEstoqueCommandHandler(db, TenantIntegracao.Contexto())
            .Handle(new RegistrarMovimentacaoEstoqueCommand(produtoId, TipoMovimentacaoEstoque.Entrada, estoque, "Estoque inicial"), CancellationToken.None);
        return produtoId;
    }

    [Fact]
    public async Task ListarVendas_DeveRetornarSomenteNaoExcluidas()
    {
        using var db = fixture.CreateDbContext();
        var produtoId = await CriarProdutoComEstoqueAsync(db);

        var venda1 = await new RegistrarVendaCommandHandler(db, TenantIntegracao.Contexto()).Handle(
            new RegistrarVendaCommand(null, "Maria Souza", "11988887777", "Rua A, 1", FormaPagamento.Dinheiro, 0m,
                [new ItemVendaInput(produtoId, 2)]),
            CancellationToken.None);

        var venda2 = await new RegistrarVendaCommandHandler(db, TenantIntegracao.Contexto()).Handle(
            new RegistrarVendaCommand(null, "João Silva", "11999998888", "Rua B, 2", FormaPagamento.Pix, 0m,
                [new ItemVendaInput(produtoId, 1)]),
            CancellationToken.None);

        // Exclui a venda2.
        await new ExcluirVendaCommandHandler(db, TenantIntegracao.Contexto(), TimeProvider.System).Handle(new ExcluirVendaCommand(venda2.VendaId), CancellationToken.None);

        var listadas = await new ListarVendasQueryHandler(db, TenantIntegracao.Contexto()).Handle(new ListarVendasQuery(), CancellationToken.None);

        Assert.Single(listadas);
        Assert.Equal(venda1.VendaId, listadas[0].Id);
    }

    [Fact]
    public async Task EditarVenda_Fiado_DeveRecalcularSaldoDevedorEEstoque()
    {
        using var db = fixture.CreateDbContext();
        var produtoId = await CriarProdutoComEstoqueAsync(db, estoque: 20);

        var resultado = await new RegistrarVendaCommandHandler(db, TenantIntegracao.Contexto()).Handle(
            new RegistrarVendaCommand(null, "Maria Souza", "11988887777", "Rua A, 1", FormaPagamento.Fiado, 0m,
                [new ItemVendaInput(produtoId, 3)]), // 300 fiado
            CancellationToken.None);

        var clientes = await new ListarClientesQueryHandler(db, TenantIntegracao.Contexto()).Handle(new ListarClientesQuery(), CancellationToken.None);
        var clienteId = clientes.Single().Id;
        Assert.Equal(300m, clientes.Single().SaldoDevedor);

        // Edita para 1 unidade (100) — o saldo deve recalcular para 100.
        var editado = await new EditarVendaCommandHandler(db, TenantIntegracao.Contexto(), TimeProvider.System).Handle(
            new EditarVendaCommand(resultado.VendaId, FormaPagamento.Fiado, 0m,
                [new ItemVendaInput(produtoId, 1)]),
            CancellationToken.None);

        Assert.Equal(100m, editado.ValorTotal);
        Assert.Equal(100m, editado.SaldoDevedor);

        // Estoque: 20 - 3 (venda antiga) + 3 (estorno) - 1 (nova) = 19.
        var produtos = await new ListarProdutosQueryHandler(db, TenantIntegracao.Contexto()).Handle(new ListarProdutosQuery(), CancellationToken.None);
        Assert.Equal(19, produtos.Single().QuantidadeEstoque);

        // Só uma venda ativa (a antiga ficou excluída).
        var vendas = await new ListarVendasQueryHandler(db, TenantIntegracao.Contexto()).Handle(new ListarVendasQuery(), CancellationToken.None);
        var vendaAtiva = Assert.Single(vendas);
        Assert.Equal(editado.VendaId, vendaAtiva.Id);
    }

    [Fact]
    public async Task ExcluirVenda_Fiado_DeveEstornarSaldoEEstoque()
    {
        using var db = fixture.CreateDbContext();
        var produtoId = await CriarProdutoComEstoqueAsync(db, estoque: 10);

        var resultado = await new RegistrarVendaCommandHandler(db, TenantIntegracao.Contexto()).Handle(
            new RegistrarVendaCommand(null, "Maria Souza", "11988887777", "Rua A, 1", FormaPagamento.Fiado, 0m,
                [new ItemVendaInput(produtoId, 2)]), // 200 fiado
            CancellationToken.None);

        var clientes = await new ListarClientesQueryHandler(db, TenantIntegracao.Contexto()).Handle(new ListarClientesQuery(), CancellationToken.None);
        Assert.Equal(200m, clientes.Single().SaldoDevedor);

        await new ExcluirVendaCommandHandler(db, TenantIntegracao.Contexto(), TimeProvider.System).Handle(new ExcluirVendaCommand(resultado.VendaId), CancellationToken.None);

        clientes = await new ListarClientesQueryHandler(db, TenantIntegracao.Contexto()).Handle(new ListarClientesQuery(), CancellationToken.None);
        Assert.Equal(0m, clientes.Single().SaldoDevedor);

        var produtos = await new ListarProdutosQueryHandler(db, TenantIntegracao.Contexto()).Handle(new ListarProdutosQuery(), CancellationToken.None);
        Assert.Equal(10, produtos.Single().QuantidadeEstoque); // estoque restaurado

        // Venda permanece no banco mas não lista ativa.
        var vendas = await new ListarVendasQueryHandler(db, TenantIntegracao.Contexto()).Handle(new ListarVendasQuery(), CancellationToken.None);
        Assert.Empty(vendas);
        using (var dbCheck = fixture.CreateDbContext())
        {
            var venda = dbCheck.Vendas.Find(resultado.VendaId);
            Assert.NotNull(venda!.ExcluidaEm);
        }
    }

    [Fact]
    public async Task ExcluirVenda_FiadoComSaldoInsuficiente_DeveBloquear()
    {
        using var db = fixture.CreateDbContext();
        var produtoId = await CriarProdutoComEstoqueAsync(db, estoque: 10);

        var resultado = await new RegistrarVendaCommandHandler(db, TenantIntegracao.Contexto()).Handle(
            new RegistrarVendaCommand(null, "Maria Souza", "11988887777", "Rua A, 1", FormaPagamento.Fiado, 0m,
                [new ItemVendaInput(produtoId, 2)]), // 200 fiado
            CancellationToken.None);

        // Cliente paga 150 -> saldo 50 < 200 (não cobre a venda).
        var clientes = await new ListarClientesQueryHandler(db, TenantIntegracao.Contexto()).Handle(new ListarClientesQuery(), CancellationToken.None);
        var clienteId = clientes.Single().Id;
        await new RevendaApp.Web.Features.Clientes.Pagar.RegistrarPagamentoCommandHandler(db, TenantIntegracao.Contexto(), new RevendaApp.Web.Common.VendasAtualizadasNotifier())
            .Handle(new RevendaApp.Web.Features.Clientes.Pagar.RegistrarPagamentoCommand(clienteId, 150m, FormaPagamento.Dinheiro), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ExcluirVendaCommandHandler(db, TenantIntegracao.Contexto(), TimeProvider.System).Handle(new ExcluirVendaCommand(resultado.VendaId), CancellationToken.None).AsTask());
    }
}