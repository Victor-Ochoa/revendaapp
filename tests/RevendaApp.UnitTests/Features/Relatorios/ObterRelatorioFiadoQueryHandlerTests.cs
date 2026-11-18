using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Common.Relatorios;
using RevendaApp.Web.Features.Relatorios.Fiado;

namespace RevendaApp.UnitTests.Features.Relatorios;

public class ObterRelatorioFiadoQueryHandlerTests
{
    private static readonly PeriodoMes Novembro2026 = new(2026, 11);

    /// <summary>"Hoje" fixo: 17/11/2026 12:00 UTC (09:00 em Brasília).</summary>
    private static readonly DateTime Hoje = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    private static ObterRelatorioFiadoQueryHandler CriarHandler(
        RevendaApp.Infrastructure.AppDbContext db, DateTime? hoje = null) =>
        new(db, TenantTeste.Contexto(), TimeProviderFixo.Em(hoje ?? Hoje));

    private static async Task<(RevendaApp.Infrastructure.AppDbContext Db, Cliente Cliente, Produto Produto)>
        CriarBaseAsync()
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria", "11988887777", "Rua A, 1");
        db.Clientes.Add(cliente);
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 5);
        db.Produtos.Add(produto);
        await db.SaveChangesAsync();
        return (db, cliente, produto);
    }

    private static Venda VendaFiado(Cliente cliente, Produto produto, decimal valor, DateTime dataHora)
    {
        var venda = new Venda(cliente.Id, FormaPagamento.Fiado, dataHora);
        venda.AdicionarItem(produto.Id, 1, valor, 60m);
        return venda;
    }

    [Fact]
    public async Task Fiado_usa_saldo_fiado_e_nao_o_valor_total_no_aging()
    {
        var (db, cliente, produto) = await CriarBaseAsync();
        var venda = VendaFiado(cliente, produto, 100m, new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc));
        venda.AbaterFiado(60m);
        db.Vendas.Add(venda);
        cliente.AdicionarSaldoDevedor(40m);
        await db.SaveChangesAsync();

        var resultado = await CriarHandler(db).Handle(
            new ObterRelatorioFiadoQuery(Novembro2026), CancellationToken.None);

        Assert.Equal(40m, resultado.Clientes.Single().Saldo);
    }

    [Fact]
    public async Task Fiado_calcula_faixas_de_atraso_pela_venda_mais_antiga_em_aberto()
    {
        var (db, cliente, produto) = await CriarBaseAsync();
        // 45 dias antes de 17/11/2026 → 03/10/2026.
        db.Vendas.Add(VendaFiado(cliente, produto, 100m, new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc)));
        cliente.AdicionarSaldoDevedor(100m);
        await db.SaveChangesAsync();

        var resultado = await CriarHandler(db).Handle(
            new ObterRelatorioFiadoQuery(Novembro2026), CancellationToken.None);

        Assert.Equal("31–60", resultado.Faixas.Single(f => f.Clientes > 0).Rotulo);
        Assert.Equal(45, resultado.Clientes.Single().DiasAtraso);
    }

    [Fact]
    public async Task Fiado_marca_aviso_quando_saldo_do_cliente_difere_das_vendas()
    {
        var (db, cliente, produto) = await CriarBaseAsync();
        db.Vendas.Add(VendaFiado(cliente, produto, 100m, new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc)));
        cliente.AdicionarSaldoDevedor(30m);
        await db.SaveChangesAsync();

        var resultado = await CriarHandler(db).Handle(
            new ObterRelatorioFiadoQuery(Novembro2026), CancellationToken.None);

        Assert.True(resultado.Clientes.Single().TemDivergenciaDeSaldo);
    }

    [Fact]
    public async Task Fiado_soma_recebimentos_do_mes_por_forma()
    {
        var (db, cliente, _) = await CriarBaseAsync();
        db.Pagamentos.Add(new Pagamento(cliente.Id, 50m, FormaPagamento.Dinheiro,
            data: new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var resultado = await CriarHandler(db).Handle(
            new ObterRelatorioFiadoQuery(Novembro2026), CancellationToken.None);

        Assert.Equal(50m, resultado.TotalRecebido);
        Assert.Equal(50m, resultado.RecebimentosPorForma.Single().Valor);
    }

    [Fact]
    public async Task Fiado_mes_sem_movimento_retorna_zerado()
    {
        var (db, cliente, produto) = await CriarBaseAsync();
        db.Vendas.Add(VendaFiado(cliente, produto, 100m, new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc)));
        cliente.AdicionarSaldoDevedor(100m);
        await db.SaveChangesAsync();

        var resultado = await CriarHandler(db).Handle(
            new ObterRelatorioFiadoQuery(Novembro2026), CancellationToken.None);

        Assert.Equal(0m, resultado.TotalRecebido);
        Assert.Equal(30, resultado.RecebimentosPorDia.Count);
        Assert.All(resultado.RecebimentosPorDia, r => Assert.Equal(0m, r.Valor));
        Assert.Single(resultado.Faixas, f => f.Clientes > 0);
    }

    [Fact]
    public async Task Fiado_nao_conta_pagamento_de_venda_no_ato_como_recebimento_de_fiado()
    {
        // Pagamento com VendaId != null é pagamento no ato de venda não-fiado; não é recebimento
        // de fiado. Contá-lo inflaria o total recebido do mês.
        var (db, cliente, _) = await CriarBaseAsync();
        db.Pagamentos.Add(new Pagamento(cliente.Id, 80m, FormaPagamento.Pix,
            vendaId: Guid.NewGuid(), data: new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var resultado = await CriarHandler(db).Handle(
            new ObterRelatorioFiadoQuery(Novembro2026), CancellationToken.None);

        Assert.Equal(0m, resultado.TotalRecebido);
    }

    [Fact]
    public async Task Fiado_sem_empresa_selecionada_lanca()
    {
        var tenant = new FakeTenantContext(empresaId: null, ehSuperAdmin: true);
        await using var db = InMemoryDbContextFactory.Create(tenant: tenant);
        var handler = new ObterRelatorioFiadoQueryHandler(db, tenant, TimeProviderFixo.Em(Hoje));

        var excecao = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(new ObterRelatorioFiadoQuery(Novembro2026), CancellationToken.None).AsTask());

        Assert.Contains("exige uma empresa", excecao.Message);
    }
}