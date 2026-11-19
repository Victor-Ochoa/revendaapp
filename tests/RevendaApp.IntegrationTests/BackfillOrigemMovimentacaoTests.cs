using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.IntegrationTests.TestHelpers;

namespace RevendaApp.IntegrationTests.Tests;

/// <summary>
/// O backfill da migração <c>OrigemMovimentacaoEstoque</c> classifica as movimentações anteriores à
/// coluna. A regra é determinística por construção do código (só quatro caminhos criam movimento),
/// mas errar a ordem dos UPDATEs — marcar "tudo que é Entrada é compra" antes de marcar o estorno —
/// transformaria 21 estornos de venda em compra, inflando o total comprado. Os testes rodam o mesmo
/// SQL da migração contra um Postgres real, nas duas ordens que importam.
/// </summary>
public class BackfillOrigemMovimentacaoTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    /// <summary>Mesmo SQL do <c>Up</c> da migração — duplicado de propósito, para o teste falhar
    /// se o SQL da migração mudar sem o comportamento esperado. A ordem é significativa.</summary>
    private static readonly string[] SqlBackfill =
    [
        """UPDATE "MovimentacoesEstoque" SET "Origem" = 'EstornoVenda' WHERE "Tipo" = 'Entrada' AND "Motivo" = 'Estorno de venda excluída';""",
        """UPDATE "MovimentacoesEstoque" SET "Origem" = 'Venda' WHERE "Tipo" = 'Saida' AND "Motivo" = 'Venda';""",
        """UPDATE "MovimentacoesEstoque" SET "Origem" = 'Compra' WHERE "Tipo" = 'Entrada' AND "Origem" = 'Manual';""",
    ];

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Produto CriarProduto(RevendaApp.Infrastructure.AppDbContext db)
    {
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 50m, 30m, 5);
        produto.RegistrarEntrada(50);
        db.Produtos.Add(produto);
        return produto;
    }

    /// <summary>Zera a coluna em todas as linhas, simulando o estado anterior à migração.</summary>
    private static async Task VoltarAoEstadoAnteriorAsync(RevendaApp.Infrastructure.AppDbContext db) =>
        await db.Database.ExecuteSqlRawAsync("""UPDATE "MovimentacoesEstoque" SET "Origem" = 'Manual';""");

    private static async Task ExecutarBackfillAsync(RevendaApp.Infrastructure.AppDbContext db)
    {
        foreach (var sql in SqlBackfill)
        {
            await db.Database.ExecuteSqlRawAsync(sql);
        }
    }

    [Fact]
    public async Task Backfill_classifica_estorno_de_venda_antes_de_marcar_compra()
    {
        await using var db = fixture.CreateDbContext();
        var produto = CriarProduto(db);

        var estorno = new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Entrada, 10,
            "Estorno de venda excluída", origem: OrigemMovimentacaoEstoque.Manual);
        var compra = new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Entrada, 50,
            "COMPRA", origem: OrigemMovimentacaoEstoque.Manual);
        var compraSemMotivo = new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Entrada, 30,
            null, origem: OrigemMovimentacaoEstoque.Manual);
        db.MovimentacoesEstoque.AddRange(estorno, compra, compraSemMotivo);
        await db.SaveChangesAsync();

        await VoltarAoEstadoAnteriorAsync(db);
        await ExecutarBackfillAsync(db);

        var origens = await db.MovimentacoesEstoque.AsNoTracking()
            .ToDictionaryAsync(m => m.Id, m => m.Origem);

        Assert.Equal(OrigemMovimentacaoEstoque.EstornoVenda, origens[estorno.Id]);
        Assert.Equal(OrigemMovimentacaoEstoque.Compra, origens[compra.Id]);
        Assert.Equal(OrigemMovimentacaoEstoque.Compra, origens[compraSemMotivo.Id]);
    }

    [Fact]
    public async Task Backfill_classifica_saida_de_venda_e_preserva_saida_manual()
    {
        await using var db = fixture.CreateDbContext();
        var produto = CriarProduto(db);

        var venda = new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Saida, 4,
            "Venda", origem: OrigemMovimentacaoEstoque.Manual);
        var saidaManual = new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Saida, 2,
            "DEVOLVIDO EDER", origem: OrigemMovimentacaoEstoque.Manual);
        db.MovimentacoesEstoque.AddRange(venda, saidaManual);
        await db.SaveChangesAsync();

        await VoltarAoEstadoAnteriorAsync(db);
        await ExecutarBackfillAsync(db);

        var origens = await db.MovimentacoesEstoque.AsNoTracking()
            .ToDictionaryAsync(m => m.Id, m => m.Origem);

        Assert.Equal(OrigemMovimentacaoEstoque.Venda, origens[venda.Id]);
        // Saída que não é venda mantém o default da coluna: não é compra nem venda.
        Assert.Equal(OrigemMovimentacaoEstoque.Manual, origens[saidaManual.Id]);
    }

    /// <summary>
    /// O total comprado depois do backfill tem que bater com a soma das entradas que não são
    /// estorno — é exatamente o número que o relatório passa a mostrar no filtro "Entradas".
    /// </summary>
    [Fact]
    public async Task Backfill_total_de_compra_exclui_estornos()
    {
        await using var db = fixture.CreateDbContext();
        var produto = CriarProduto(db);

        db.MovimentacoesEstoque.AddRange(
            new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Entrada, 40,
                "COMPRA", origem: OrigemMovimentacaoEstoque.Manual),
            new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Entrada, 25,
                null, origem: OrigemMovimentacaoEstoque.Manual),
            new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Entrada, 15,
                "Estorno de venda excluída", origem: OrigemMovimentacaoEstoque.Manual),
            new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Saida, 9,
                "Venda", origem: OrigemMovimentacaoEstoque.Manual));
        await db.SaveChangesAsync();

        await VoltarAoEstadoAnteriorAsync(db);
        await ExecutarBackfillAsync(db);

        var totalComprado = await db.MovimentacoesEstoque.AsNoTracking()
            .Where(m => m.Tipo == TipoMovimentacaoEstoque.Entrada
                        && m.Origem == OrigemMovimentacaoEstoque.Compra)
            .SumAsync(m => m.Quantidade);

        Assert.Equal(65, totalComprado);   // 40 + 25; o estorno de 15 fica de fora
    }
}