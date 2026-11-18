using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Common.Relatorios;
using RevendaApp.Web.Features.Relatorios.Vasilhames;

namespace RevendaApp.UnitTests.Features.Relatorios;

public class ObterRelatorioVasilhamesQueryHandlerTests
{
    private static readonly PeriodoMes Novembro2026 = new(2026, 11);

    private static async Task<(RevendaApp.Infrastructure.AppDbContext Db, Cliente Cliente, Produto Produto)>
        CriarBaseAsync()
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria", "11988887777", "Rua A, 1");
        db.Clientes.Add(cliente);
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 5, controlaVasilhame: true);
        db.Produtos.Add(produto);
        await db.SaveChangesAsync();
        return (db, cliente, produto);
    }

    private static ObterRelatorioVasilhamesQueryHandler CriarHandler(RevendaApp.Infrastructure.AppDbContext db) =>
        new(db, TenantTeste.Contexto());

    [Fact]
    public async Task Vasilhames_usa_historico_para_devolucoes_parciais_do_mes()
    {
        var (db, cliente, produto) = await CriarBaseAsync();
        db.EmprestimosVasilhames.Add(new EmprestimoVasilhame(cliente.Id, produto.Id, 5,
            new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc)));
        // Devolução parcial de 2 no mês: o empréstimo segue com DevolvidoEm null.
        db.HistoricosEstoque.Add(new HistoricoEstoque(produto.Id, TipoHistoricoEstoque.DevolucaoVasilhame,
            "Devolução de 2 vasilhames", "Devolução", "sistema", quantidade: 2,
            data: new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var resultado = await CriarHandler(db).Handle(
            new ObterRelatorioVasilhamesQuery(Novembro2026), CancellationToken.None);

        Assert.Equal(2, resultado.TotalDevolvidoNoMes);
    }

    [Fact]
    public async Task Vasilhames_lista_apenas_produtos_que_controlam_vasilhame()
    {
        var (db, _, _) = await CriarBaseAsync();
        db.Produtos.Add(new Produto("Água 20L", TipoProduto.Agua, 20m, 10m, 15m, controlaVasilhame: false));
        await db.SaveChangesAsync();

        var resultado = await CriarHandler(db).Handle(
            new ObterRelatorioVasilhamesQuery(Novembro2026), CancellationToken.None);

        Assert.Single(resultado.PorProduto);
    }

    [Fact]
    public async Task Vasilhames_soma_em_aberto_por_cliente_e_produto()
    {
        var (db, cliente, produto) = await CriarBaseAsync();
        db.EmprestimosVasilhames.AddRange(
            new EmprestimoVasilhame(cliente.Id, produto.Id, 3, new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc)),
            new EmprestimoVasilhame(cliente.Id, produto.Id, 2, new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var resultado = await CriarHandler(db).Handle(
            new ObterRelatorioVasilhamesQuery(Novembro2026), CancellationToken.None);

        Assert.Equal(5, resultado.EmAberto.Single().Saldo);
    }

    [Fact]
    public async Task Vasilhames_conta_emprestimos_do_mes()
    {
        var (db, cliente, produto) = await CriarBaseAsync();
        db.EmprestimosVasilhames.Add(new EmprestimoVasilhame(cliente.Id, produto.Id, 5,
            new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var resultado = await CriarHandler(db).Handle(
            new ObterRelatorioVasilhamesQuery(Novembro2026), CancellationToken.None);

        Assert.Equal(5, resultado.TotalEmprestadoNoMes);
    }

    [Fact]
    public async Task Vasilhames_mes_sem_movimento_retorna_zerado()
    {
        var (db, _, _) = await CriarBaseAsync();

        var resultado = await CriarHandler(db).Handle(
            new ObterRelatorioVasilhamesQuery(Novembro2026), CancellationToken.None);

        Assert.Equal(0, resultado.TotalEmprestadoNoMes);
        Assert.Equal(0, resultado.TotalDevolvidoNoMes);
        Assert.Equal(30, resultado.DevolucoesPorDia.Count);
        Assert.All(resultado.DevolucoesPorDia, d => Assert.Equal(0, d.Quantidade));
    }

    [Fact]
    public async Task Vasilhames_nao_conta_devolucao_de_fora_do_mes()
    {
        var (db, cliente, produto) = await CriarBaseAsync();
        db.EmprestimosVasilhames.Add(new EmprestimoVasilhame(cliente.Id, produto.Id, 5,
            new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc)));
        db.HistoricosEstoque.Add(new HistoricoEstoque(produto.Id, TipoHistoricoEstoque.DevolucaoVasilhame,
            "Devolução de 3 vasilhames", "Devolução", "sistema", quantidade: 3,
            data: new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var resultado = await CriarHandler(db).Handle(
            new ObterRelatorioVasilhamesQuery(Novembro2026), CancellationToken.None);

        Assert.Equal(0, resultado.TotalDevolvidoNoMes);
    }

    [Fact]
    public async Task Vasilhames_sem_empresa_selecionada_lanca()
    {
        var tenant = new FakeTenantContext(empresaId: null, ehSuperAdmin: true);
        await using var db = InMemoryDbContextFactory.Create(tenant: tenant);
        var handler = new ObterRelatorioVasilhamesQueryHandler(db, tenant);

        var excecao = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(new ObterRelatorioVasilhamesQuery(Novembro2026), CancellationToken.None).AsTask());

        Assert.Contains("exige uma empresa", excecao.Message);
    }
}