using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Common.Relatorios;
using RevendaApp.Web.Features.Relatorios.Vendas;

namespace RevendaApp.UnitTests.Features.Relatorios;

public class ObterRelatorioVendasQueryHandlerTests
{
    private static readonly PeriodoMes Novembro2026 = new(2026, 11);

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

    private static Venda NovaVenda(Cliente cliente, Produto produto, FormaPagamento forma, decimal valor, DateTime dataHora)
    {
        var venda = new Venda(cliente.Id, forma, dataHora);
        venda.AdicionarItem(produto.Id, 1, valor, 60m);
        return venda;
    }

    [Fact]
    public async Task Vendas_agrupa_por_dia_de_brasilia()
    {
        var (db, cliente, produto) = await CriarBaseAsync();
        // 02/11 02:00 UTC = 01/11 23:00 BRT → conta no dia 1.
        db.Vendas.Add(NovaVenda(cliente, produto, FormaPagamento.Pix, 100m,
            new DateTime(2026, 11, 2, 2, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        var handler = new ObterRelatorioVendasQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioVendasQuery(Novembro2026), CancellationToken.None);

        Assert.Equal(1, resultado.PorDia.Single(d => d.Dia == 1).Quantidade);
    }

    [Fact]
    public async Task Vendas_nao_reconhece_lucro_de_fiado_em_aberto()
    {
        var (db, cliente, produto) = await CriarBaseAsync();
        db.Vendas.Add(NovaVenda(cliente, produto, FormaPagamento.Fiado, 100m,
            new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        var handler = new ObterRelatorioVendasQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioVendasQuery(Novembro2026), CancellationToken.None);

        Assert.Equal(0m, resultado.LucroTotal);
    }

    [Fact]
    public async Task Vendas_reconhece_lucro_proporcional_ao_pago_no_fiado()
    {
        var (db, cliente, produto) = await CriarBaseAsync();
        var venda = NovaVenda(cliente, produto, FormaPagamento.Fiado, 100m,
            new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc));
        venda.AbaterFiado(50m);
        db.Vendas.Add(venda);
        await db.SaveChangesAsync();
        var handler = new ObterRelatorioVendasQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioVendasQuery(Novembro2026), CancellationToken.None);

        // Lucro total da venda = 100 − 60 = 40; metade paga → 20.
        Assert.Equal(20m, resultado.LucroTotal);
    }

    [Fact]
    public async Task Vendas_exclui_venda_excluida_logicamente()
    {
        var (db, cliente, produto) = await CriarBaseAsync();
        var venda = NovaVenda(cliente, produto, FormaPagamento.Pix, 100m,
            new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc));
        venda.Excluir();
        db.Vendas.Add(venda);
        await db.SaveChangesAsync();
        var handler = new ObterRelatorioVendasQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioVendasQuery(Novembro2026), CancellationToken.None);

        Assert.Equal(0, resultado.QuantidadeVendas);
        Assert.Equal(0m, resultado.FaturamentoTotal);
    }

    [Fact]
    public async Task Vendas_calcula_ticket_medio_e_zero_sem_vendas()
    {
        var (db, _, _) = await CriarBaseAsync();
        var handler = new ObterRelatorioVendasQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioVendasQuery(Novembro2026), CancellationToken.None);

        Assert.Equal(0m, resultado.TicketMedio);
    }

    [Fact]
    public async Task Vendas_calcula_ticket_medio()
    {
        var (db, cliente, produto) = await CriarBaseAsync();
        db.Vendas.AddRange(
            NovaVenda(cliente, produto, FormaPagamento.Pix, 100m, new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc)),
            NovaVenda(cliente, produto, FormaPagamento.Dinheiro, 50m, new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        var handler = new ObterRelatorioVendasQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioVendasQuery(Novembro2026), CancellationToken.None);

        Assert.Equal(75m, resultado.TicketMedio);
    }

    [Fact]
    public async Task Vendas_agrupa_por_forma_de_pagamento()
    {
        var (db, cliente, produto) = await CriarBaseAsync();
        db.Vendas.AddRange(
            NovaVenda(cliente, produto, FormaPagamento.Pix, 100m, new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc)),
            NovaVenda(cliente, produto, FormaPagamento.Dinheiro, 50m, new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        var handler = new ObterRelatorioVendasQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioVendasQuery(Novembro2026), CancellationToken.None);

        Assert.Equal(2, resultado.PorForma.Count);
        Assert.Equal(100m, resultado.PorForma.Single(f => f.Forma == FormaPagamento.Pix).Valor);
        Assert.Equal(50m, resultado.PorForma.Single(f => f.Forma == FormaPagamento.Dinheiro).Valor);
    }

    [Fact]
    public async Task Vendas_sem_empresa_selecionada_lanca()
    {
        var tenant = new FakeTenantContext(empresaId: null, ehSuperAdmin: true);
        await using var db = InMemoryDbContextFactory.Create(tenant: tenant);
        var handler = new ObterRelatorioVendasQueryHandler(db, tenant);

        var excecao = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(new ObterRelatorioVendasQuery(Novembro2026), CancellationToken.None).AsTask());

        Assert.Contains("exige uma empresa", excecao.Message);
    }
}