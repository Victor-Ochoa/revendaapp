using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.IntegrationTests.TestHelpers;

namespace RevendaApp.IntegrationTests.Tests;

/// <summary>
/// O backfill da migração <c>ValorPagoFiadoNaVenda</c> precisa marcar como integralmente pagas
/// as vendas fiado que já estavam quitadas antes da coluna existir — senão o dashboard passaria a
/// reconhecer zero de lucro nelas (a regra proporcional de <c>VendaLucro</c> veria 0% pago).
/// Os testes rodam o mesmo SQL da migração contra um Postgres real.
/// </summary>
public class BackfillFiadoTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    /// <summary>Mesmo comando do <c>Up</c> da migração — duplicado de propósito, para o teste
    /// falhar se o SQL da migração mudar sem o comportamento esperado.</summary>
    private const string SqlBackfill = """
        UPDATE "Vendas" v
        SET "ValorPagoFiado" = itens."ValorBruto" - v."Desconto" + v."ValorEntrega"
        FROM (
            SELECT "VendaId", SUM("PrecoUnitario" * "Quantidade") AS "ValorBruto"
            FROM "ItensVenda"
            GROUP BY "VendaId"
        ) itens
        WHERE itens."VendaId" = v."Id"
          AND v."FormaPagamento" = 'Fiado'
          AND v."Status" = 'FiadoQuitado';
        """;

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static (Cliente Cliente, Produto Produto) CriarBase(RevendaApp.Infrastructure.AppDbContext db)
    {
        var cliente = new Cliente("Maria", "11988887777", "Rua A, 1");
        db.Clientes.Add(cliente);

        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 50m, 30m, 5);
        produto.RegistrarEntrada(50);
        db.Produtos.Add(produto);

        return (cliente, produto);
    }

    [Fact]
    public async Task Backfill_marca_vendas_fiado_quitadas_com_valor_total()
    {
        await using var db = fixture.CreateDbContext();
        var (cliente, produto) = CriarBase(db);

        // Venda fiado: 2 itens de 50 (bruto 100), desconto 10, entrega 5 → total 95.
        var venda = new Venda(cliente.Id, FormaPagamento.Fiado);
        venda.AdicionarItem(produto.Id, 1, 50m, 30m);
        venda.AdicionarItem(produto.Id, 1, 50m, 30m);
        venda.AplicarDesconto(10m);
        venda.DefinirValorEntrega(5m);
        venda.MarcarFiadoQuitado();
        db.Vendas.Add(venda);
        await db.SaveChangesAsync();

        // Zera a coluna como estava antes da migração existir.
        await db.Database.ExecuteSqlRawAsync(
            """UPDATE "Vendas" SET "ValorPagoFiado" = 0 WHERE "Id" = {0}""", venda.Id);

        await db.Database.ExecuteSqlRawAsync(SqlBackfill);

        // ValorTotal/SaldoFiado são calculados sobre os itens: sem o Include, ambos saem errados.
        var vendaRecarregada = await db.Vendas.AsNoTracking()
            .Include(v => v.Itens)
            .SingleAsync(v => v.Id == venda.Id);
        Assert.Equal(95m, vendaRecarregada.ValorPagoFiado);
        Assert.Equal(0m, vendaRecarregada.SaldoFiado);
    }

    [Fact]
    public async Task Backfill_nao_altera_venda_fiado_aberta()
    {
        await using var db = fixture.CreateDbContext();
        var (cliente, produto) = CriarBase(db);

        var venda = new Venda(cliente.Id, FormaPagamento.Fiado);
        venda.AdicionarItem(produto.Id, 1, 50m, 30m);
        db.Vendas.Add(venda);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(SqlBackfill);

        var vendaRecarregada = await db.Vendas.AsNoTracking()
            .Include(v => v.Itens)
            .SingleAsync(v => v.Id == venda.Id);
        Assert.Equal(0m, vendaRecarregada.ValorPagoFiado);
        Assert.Equal(50m, vendaRecarregada.SaldoFiado);
    }

    [Fact]
    public async Task Backfill_nao_altera_venda_paga_no_ato()
    {
        // A venda não-fiado já reconhece lucro integral por não ser fiado; o backfill não pode
        // tocar a coluna dela (VendaLucro ignora ValorPagoFiado nesse caso, mas um valor espúrio
        // confundiria relatórios de fiado que filtram por ValorPagoFiado > 0).
        await using var db = fixture.CreateDbContext();
        var (cliente, produto) = CriarBase(db);

        var venda = new Venda(cliente.Id, FormaPagamento.Pix);
        venda.AdicionarItem(produto.Id, 1, 50m, 30m);
        db.Vendas.Add(venda);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(SqlBackfill);

        var vendaRecarregada = await db.Vendas.AsNoTracking().SingleAsync(v => v.Id == venda.Id);
        Assert.Equal(0m, vendaRecarregada.ValorPagoFiado);
    }
}