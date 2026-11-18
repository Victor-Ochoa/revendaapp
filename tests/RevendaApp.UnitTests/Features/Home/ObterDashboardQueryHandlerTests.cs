using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.Infrastructure;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Common;
using RevendaApp.Web.Features.Home;

namespace RevendaApp.UnitTests.Features.Home;

public class ObterDashboardQueryHandlerTests
{
    private static readonly DateTime FixedNow = new(2026, 9, 15, 14, 0, 0, DateTimeKind.Utc);

    private static async Task<(AppDbContext db, Cliente cliente, Produto produto1, Produto produto2)> CriarBaseAsync()
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);

        var cliente = new Cliente("Maria Souza", "11999999999", "Rua A, 10");
        var produto1 = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m, estoqueMinimo: 5);
        produto1.RegistrarEntrada(2);

        var produto2 = new Produto("Água 20L", TipoProduto.Agua, 25m, 12m, 18m, estoqueMinimo: 10);
        produto2.RegistrarEntrada(6);

        db.Clientes.Add(cliente);
        db.Produtos.AddRange(produto1, produto2);
        await db.SaveChangesAsync();

        return (db, cliente, produto1, produto2);
    }

    [Fact]
    public async Task Handle_PeriodoHoje_DeveFiltrarVendasDeHojeEAgregarPorHora()
    {
        var (db, cliente, produto1, _) = await CriarBaseAsync();

        var vendaHoje = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc));
        vendaHoje.AdicionarItem(produto1.Id, 2, 100m, 60m);
        vendaHoje.AplicarDesconto(20m);

        var vendaDiaAnterior = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc));
        vendaDiaAnterior.AdicionarItem(produto1.Id, 1, 100m, 60m);

        db.Vendas.AddRange(vendaHoje, vendaDiaAnterior);
        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        Assert.Equal(180m, result.VendasTotal);
        Assert.Equal(1, result.VendasQuantidade);
        Assert.Equal(60m, result.LucroTotal);
        Assert.Equal(20m, result.DescontoTotal);

        var periodo = Assert.Single(result.VendasPorPeriodo);
        Assert.Equal("09h", periodo.Rotulo);
        Assert.Equal(180m, periodo.Total);
        Assert.Equal(60m, periodo.Lucro);

        Assert.Equal(2, result.ProdutosEstoqueBaixo.Count);
    }

    [Fact]
    public async Task Handle_PeriodoEsteMes_DeveAgregarPorDia()
    {
        var (db, cliente, produto1, produto2) = await CriarBaseAsync();

        var vendaHoje = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc));
        vendaHoje.AdicionarItem(produto1.Id, 2, 100m, 60m);
        vendaHoje.AplicarDesconto(20m);

        var vendaMes = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc));
        vendaMes.AdicionarItem(produto2.Id, 2, 25m, 12m);

        var vendaMesPassado = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 8, 29, 9, 0, 0, DateTimeKind.Utc));
        vendaMesPassado.AdicionarItem(produto1.Id, 1, 100m, 60m);

        db.Vendas.AddRange(vendaHoje, vendaMes, vendaMesPassado);
        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.EsteMes), CancellationToken.None);

        Assert.Equal(230m, result.VendasTotal);
        Assert.Equal(2, result.VendasQuantidade);
        Assert.Equal(86m, result.LucroTotal);
        Assert.Equal(20m, result.DescontoTotal);

        Assert.Equal(2, result.VendasPorPeriodo.Count);
        Assert.Equal("05/09", result.VendasPorPeriodo[0].Rotulo);
        Assert.Equal(50m, result.VendasPorPeriodo[0].Total);
        Assert.Equal(26m, result.VendasPorPeriodo[0].Lucro);

        Assert.Equal("15/09", result.VendasPorPeriodo[1].Rotulo);
        Assert.Equal(180m, result.VendasPorPeriodo[1].Total);
        Assert.Equal(60m, result.VendasPorPeriodo[1].Lucro);
    }

    [Fact]
    public async Task Handle_PeriodoTudo_DeveIncluirTodasAsVendas()
    {
        var (db, cliente, produto1, produto2) = await CriarBaseAsync();

        var vendaHoje = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc));
        vendaHoje.AdicionarItem(produto1.Id, 2, 100m, 60m);
        vendaHoje.AplicarDesconto(20m);

        var vendaMes = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc));
        vendaMes.AdicionarItem(produto2.Id, 2, 25m, 12m);

        var vendaMesPassado = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 8, 29, 9, 0, 0, DateTimeKind.Utc));
        vendaMesPassado.AdicionarItem(produto1.Id, 1, 100m, 60m);

        db.Vendas.AddRange(vendaHoje, vendaMes, vendaMesPassado);
        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Tudo), CancellationToken.None);

        Assert.Equal(330m, result.VendasTotal);
        Assert.Equal(3, result.VendasQuantidade);
        Assert.Equal(126m, result.LucroTotal);
    }

    [Fact]
    public async Task Handle_DeveContarVendaFiadoComoVendaMasExcluirSeuLucroDosLucrosDoPeriodo()
    {
        var (db, cliente, produto, _) = await CriarBaseAsync();

        // Venda paga hoje: entra no lucro.
        var vendaPaga = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc));
        vendaPaga.AdicionarItem(produto.Id, 2, 100m, 60m);

        // Venda fiado hoje: conta como venda, mas o lucro NÃO deve entrar no lucro do período.
        var vendaFiado = new Venda(cliente.Id, FormaPagamento.Fiado, new DateTime(2026, 9, 15, 12, 30, 0, DateTimeKind.Utc));
        vendaFiado.AdicionarItem(produto.Id, 1, 100m, 60m);

        db.Vendas.AddRange(vendaPaga, vendaFiado);
        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        // As duas vendas contam nos totais de vendas (a fiada também é contabilizada como venda).
        Assert.Equal(300m, result.VendasTotal);
        Assert.Equal(2, result.VendasQuantidade);

        // Porém o lucro da venda fiado (40m) é excluído — só o da venda paga (80m) entra.
        Assert.Equal(80m, result.LucroTotal);

        // O gráfico conta as duas vendas no total da hora, mas o lucro exibido exclui o fiado.
        var periodo = Assert.Single(result.VendasPorPeriodo);
        Assert.Equal(300m, periodo.Total);
        Assert.Equal(80m, periodo.Lucro);
    }

    [Fact]
    public async Task Handle_DeveIncluirLucroDeVendaFiadoQuitada_NosLucrosDoPeriodo()
    {
        var (db, cliente, produto, _) = await CriarBaseAsync();

        // Venda fiado ainda aberta (não paga): lucro NÃO entra.
        var vendaFiadoAberto = new Venda(cliente.Id, FormaPagamento.Fiado, new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc));
        vendaFiadoAberto.AdicionarItem(produto.Id, 1, 100m, 60m);

        // Venda fiado já quitada (paga hoje): lucro DEVE entrar.
        var vendaFiadoQuitado = new Venda(cliente.Id, FormaPagamento.Fiado, new DateTime(2026, 9, 15, 13, 0, 0, DateTimeKind.Utc));
        vendaFiadoQuitado.AdicionarItem(produto.Id, 2, 100m, 60m);
        vendaFiadoQuitado.MarcarFiadoQuitado();

        db.Vendas.AddRange(vendaFiadoAberto, vendaFiadoQuitado);
        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        // As duas vendas contam nos totais de vendas.
        Assert.Equal(300m, result.VendasTotal);
        Assert.Equal(2, result.VendasQuantidade);

        // Só o lucro da fiado quitada (2 x 40 = 80m) entra; o da fiado aberta (40m) fica fora.
        Assert.Equal(80m, result.LucroTotal);
    }

    [Fact]
    public async Task Handle_DeveReconhecerLucroProporcionalDeFiadoParcialmentePago()
    {
        var (db, cliente, produto, _) = await CriarBaseAsync();

        // Venda fiado 100, custo 60 (lucro 40), com 50 já alocados: metade do lucro é reconhecida.
        // Antes da quitação parcial por venda o fiado era tudo-ou-nada e esse lucro nunca aparecia.
        var vendaFiado = new Venda(cliente.Id, FormaPagamento.Fiado, new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc));
        vendaFiado.AdicionarItem(produto.Id, 1, 100m, 60m);
        vendaFiado.AbaterFiado(50m);

        db.Vendas.Add(vendaFiado);
        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        Assert.Equal(100m, result.VendasTotal);
        Assert.Equal(20m, result.LucroTotal);
        Assert.Equal(20m, Assert.Single(result.VendasPorPeriodo).Lucro);
    }

    [Fact]
    public async Task Handle_DeveCalcularTotalDevedor_ComoSomaDosSaldosDosClientes()
    {
        var (db, cliente, produto, _) = await CriarBaseAsync();

        cliente.AdicionarSaldoDevedor(150m);

        var outroCliente = new Cliente("João Pereira", "11988887777", "Rua B, 20");
        outroCliente.AdicionarSaldoDevedor(40m);
        db.Clientes.Add(outroCliente);

        var venda = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc));
        venda.AdicionarItem(produto.Id, 1, 100m, 60m);
        db.Vendas.Add(venda);

        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        // TotalDevedor independe do período — é a soma de todos os saldos devedores em aberto.
        Assert.Equal(190m, result.TotalDevedor);
    }

    [Fact]
    public async Task Handle_DeveRetornarDetalheDasVendasDoPeriodo_OrdenadoPorDataDesc()
    {
        var (db, cliente, produto, _) = await CriarBaseAsync();

        var vendaMaisAntiga = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc));
        vendaMaisAntiga.AdicionarItem(produto.Id, 1, 100m, 60m);

        var vendaMaisRecente = new Venda(cliente.Id, FormaPagamento.Pix, new DateTime(2026, 9, 15, 13, 0, 0, DateTimeKind.Utc));
        vendaMaisRecente.AdicionarItem(produto.Id, 2, 100m, 60m);
        vendaMaisRecente.AplicarDesconto(20m);

        var vendaForaPeriodo = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc));
        vendaForaPeriodo.AdicionarItem(produto.Id, 1, 100m, 60m);

        db.Vendas.AddRange(vendaMaisAntiga, vendaMaisRecente, vendaForaPeriodo);
        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        // A venda de ontem fica fora do período; as de hoje vêm em ordem decrescente de data.
        Assert.Equal(2, result.Vendas.Count);
        Assert.Equal("Maria Souza", result.Vendas[0].ClienteNome);
        Assert.Equal(FormaPagamento.Pix, result.Vendas[0].FormaPagamento);
        Assert.Equal(180m, result.Vendas[0].ValorTotal);
        Assert.Equal(60m, result.Vendas[0].Lucro);
        Assert.Equal(20m, result.Vendas[0].Desconto);

        Assert.Equal(FormaPagamento.Dinheiro, result.Vendas[1].FormaPagamento);
        Assert.Equal(100m, result.Vendas[1].ValorTotal);
    }

    [Fact]
    public async Task Handle_DeveRetornarPagamentosDoPeriodo_OrdenadoPorDataDesc()
    {
        var (db, cliente, produto, _) = await CriarBaseAsync();

        // Venda paga no ato (Dinheiro) gera um pagamento no período.
        var venda = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc));
        venda.AdicionarItem(produto.Id, 1, 100m, 60m);
        db.Vendas.Add(venda);

        var pagamentoAntigo = new Pagamento(cliente.Id, 100m, FormaPagamento.Dinheiro, "Venda", new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc));
        var pagamentoRecente = new Pagamento(cliente.Id, 50m, FormaPagamento.Pix, "Quitação fiado", new DateTime(2026, 9, 15, 13, 30, 0, DateTimeKind.Utc));
        var pagamentoForaPeriodo = new Pagamento(cliente.Id, 30m, FormaPagamento.Dinheiro, "Venda", new DateTime(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc));

        db.Pagamentos.AddRange(pagamentoAntigo, pagamentoRecente, pagamentoForaPeriodo);
        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        // Só os pagamentos de hoje, em ordem decrescente de data.
        Assert.Equal(2, result.Pagamentos.Count);
        Assert.Equal("Maria Souza", result.Pagamentos[0].ClienteNome);
        Assert.Equal(FormaPagamento.Pix, result.Pagamentos[0].FormaPagamento);
        Assert.Equal(50m, result.Pagamentos[0].Valor);
        Assert.Equal("Quitação fiado", result.Pagamentos[0].Observacao);

        Assert.Equal(FormaPagamento.Dinheiro, result.Pagamentos[1].FormaPagamento);
        Assert.Equal(100m, result.Pagamentos[1].Valor);
    }

    [Fact]
    public async Task Handle_ComValorEntrega_DeveIncluirNoVendasTotalSemAlterarLucro()
    {
        var (db, cliente, produto, _) = await CriarBaseAsync();

        var venda = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc));
        venda.AdicionarItem(produto.Id, 1, 100m, 60m);
        venda.DefinirValorEntrega(10m);
        db.Vendas.Add(venda);
        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        Assert.Equal(110m, result.VendasTotal);  // 100 + 10 de entrega
        Assert.Equal(40m, result.LucroTotal);    // entrega não entra no lucro

        var detalhe = Assert.Single(result.Vendas);
        Assert.Equal(110m, detalhe.ValorTotal);
        Assert.Equal(10m, detalhe.ValorEntrega);
        Assert.Equal(40m, detalhe.Lucro);
    }

    [Fact]
    public async Task Handle_DeveExcluirProdutosInativos_DaListaDeEstoqueBaixo()
    {
        var (db, _, produto1, _) = await CriarBaseAsync();
        // Produto1 tem estoque baixo (2 <= 5); ao desabilitá-lo, não deve gerar alerta de reposição.
        produto1.Desativar();
        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        Assert.DoesNotContain(result.ProdutosEstoqueBaixo, p => p.Id == produto1.Id);
        // O outro produto com estoque baixo (ativo) continua na lista.
        Assert.Contains(result.ProdutosEstoqueBaixo, p => p.Id != produto1.Id);
    }

    [Fact]
    public async Task Handle_PeriodoHoje_DeveUsarMeiaNoiteDeBrasiliaComoFronteira()
    {
        var (db, cliente, produto, _) = await CriarBaseAsync();

        // FixedNow = 2026-09-15T14:00Z (11:00 em Brasília). A fronteira de "hoje" é a meia-noite
        // local de 15/09 (03:00Z). Venda às 02:59Z = 23:59 do dia 14 em Brasília → deve ficar FORA.
        var vendaVespera = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 15, 2, 59, 0, DateTimeKind.Utc));
        vendaVespera.AdicionarItem(produto.Id, 1, 100m, 60m);

        // Venda às 03:00Z = meia-noite de 15/09 em Brasília → deve entrar em "Hoje".
        var vendaMeiaNoiteBra = new Venda(cliente.Id, FormaPagamento.Pix, new DateTime(2026, 9, 15, 3, 0, 0, DateTimeKind.Utc));
        vendaMeiaNoiteBra.AdicionarItem(produto.Id, 1, 100m, 60m);

        db.Vendas.AddRange(vendaVespera, vendaMeiaNoiteBra);
        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        Assert.Equal(1, result.VendasQuantidade);
        Assert.Equal(100m, result.VendasTotal);
        // O detalhe deve estar em hora local de Brasília (03:00Z → 00:00 de 15/09): data local.
        var detalhe = Assert.Single(result.Vendas);
        Assert.Equal(0, detalhe.DataHora.Hour);
        Assert.Equal(15, detalhe.DataHora.Day);
        Assert.Equal(9, detalhe.DataHora.Month);
    }

    [Fact]
    public async Task Handle_DeveAgruparPelaHoraDeBrasilia()
    {
        var (db, cliente, produto, _) = await CriarBaseAsync();

        // Duas vendas no mesmo instante UTC (14:00Z): em Brasília são 11:00. Ambas no grupo "11h".
        var venda1 = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 15, 14, 0, 0, DateTimeKind.Utc));
        venda1.AdicionarItem(produto.Id, 2, 100m, 60m);
        var venda2 = new Venda(cliente.Id, FormaPagamento.Pix, new DateTime(2026, 9, 15, 14, 10, 0, DateTimeKind.Utc));
        venda2.AdicionarItem(produto.Id, 1, 100m, 60m);

        db.Vendas.AddRange(venda1, venda2);
        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        var periodo = Assert.Single(result.VendasPorPeriodo);
        Assert.Equal("11h", periodo.Rotulo);
        Assert.Equal(300m, periodo.Total);
    }

    [Fact]
    public async Task Handle_DeveExcluirVendasExcluidas_DosTotaisELucro()
    {
        var (db, cliente, produto, _) = await CriarBaseAsync();

        var vendaAtiva = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc));
        vendaAtiva.AdicionarItem(produto.Id, 2, 100m, 60m);

        var vendaExcluida = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 15, 13, 0, 0, DateTimeKind.Utc));
        vendaExcluida.AdicionarItem(produto.Id, 3, 100m, 60m);
        vendaExcluida.Excluir();

        db.Vendas.AddRange(vendaAtiva, vendaExcluida);
        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        // Somente a venda ativa entra nos totais, quantidade e lucro.
        Assert.Equal(200m, result.VendasTotal);
        Assert.Equal(1, result.VendasQuantidade);
        Assert.Equal(80m, result.LucroTotal);
        Assert.Equal(200m, result.Vendas.Single().ValorTotal);
    }

    [Fact]
    public async Task Handle_DeveExcluirVendaExcluida_DoDetalheDaLista()
    {
        var (db, cliente, produto, _) = await CriarBaseAsync();

        var vendaAtiva = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc));
        vendaAtiva.AdicionarItem(produto.Id, 1, 100m, 60m);

        var vendaExcluida = new Venda(cliente.Id, FormaPagamento.Pix, new DateTime(2026, 9, 15, 13, 0, 0, DateTimeKind.Utc));
        vendaExcluida.AdicionarItem(produto.Id, 1, 100m, 60m);
        vendaExcluida.Excluir();

        db.Vendas.AddRange(vendaAtiva, vendaExcluida);
        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        Assert.Single(result.Vendas);
        Assert.Equal(FormaPagamento.Dinheiro, result.Vendas.Single().FormaPagamento);
    }

    [Fact]
    public async Task Handle_DeveAgregarVendasPorFormaDePagamento()
    {
        var (db, cliente, produto, _) = await CriarBaseAsync();

        var vendaDinheiro = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc));
        vendaDinheiro.AdicionarItem(produto.Id, 1, 100m, 60m);

        var vendaPix = new Venda(cliente.Id, FormaPagamento.Pix, new DateTime(2026, 9, 15, 13, 0, 0, DateTimeKind.Utc));
        vendaPix.AdicionarItem(produto.Id, 2, 100m, 60m);

        var vendaFiado = new Venda(cliente.Id, FormaPagamento.Fiado, new DateTime(2026, 9, 15, 14, 0, 0, DateTimeKind.Utc));
        vendaFiado.AdicionarItem(produto.Id, 1, 100m, 60m);

        db.Vendas.AddRange(vendaDinheiro, vendaPix, vendaFiado);
        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        Assert.Equal(3, result.VendasPorForma.Count);
        var fiado = result.VendasPorForma.Single(f => f.FormaPagamento == FormaPagamento.Fiado);
        Assert.Equal(100m, fiado.Valor);
        var pix = result.VendasPorForma.Single(f => f.FormaPagamento == FormaPagamento.Pix);
        Assert.Equal(200m, pix.Valor);
    }

    [Fact]
    public async Task Handle_DeveRetornarTopProdutosPorQuantidadeVendida()
    {
        var (db, cliente, produto1, produto2) = await CriarBaseAsync();

        var venda = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc));
        venda.AdicionarItem(produto1.Id, 3, 100m, 60m);   // 3 un, receita 300, lucro 120
        venda.AdicionarItem(produto2.Id, 1, 25m, 12m);    // 1 un, receita 25, lucro 13
        db.Vendas.Add(venda);

        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        var top = Assert.Single(result.TopProdutos, p => p.ProdutoId == produto1.Id);
        // O nome vem de ItemVenda.Produto: se o handler não carregar a navegação (ThenInclude), o
        // gráfico "Top produtos" inteiro é rotulado como "Produto removido" — regressão invisível
        // enquanto o teste só conferia números.
        Assert.Equal("Botijão 13kg", top.Nome);
        Assert.Equal(3, top.QuantidadeTotal);
        Assert.Equal(300m, top.Receita);
        Assert.Equal(120m, top.Lucro);
    }

    /// <summary>
    /// O fallback "Produto removido" é defensivo e só pode aparecer para item cujo produto não
    /// existe mais. Um produto ATIVO no catálogo nunca pode cair nele.
    /// </summary>
    [Fact]
    public async Task Handle_NaoDeveRotularProdutoExistenteComoRemovido()
    {
        var (db, cliente, produto1, _) = await CriarBaseAsync();

        var venda = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc));
        venda.AdicionarItem(produto1.Id, 1, 100m, 60m);
        db.Vendas.Add(venda);
        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        Assert.DoesNotContain(result.TopProdutos, p => p.Nome == "Produto removido");
    }

    [Fact]
    public async Task Handle_DeveRetornarTopDevedores_IndependenteDoPeriodo()
    {
        var (db, cliente, produto, _) = await CriarBaseAsync();

        cliente.AdicionarSaldoDevedor(150m);
        var outro = new Cliente("João Pereira", "11988887777", "Rua B, 20");
        outro.AdicionarSaldoDevedor(250m);
        db.Clientes.Add(outro);

        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        Assert.Equal(2, result.TopDevedores.Count);
        Assert.Equal("João Pereira", result.TopDevedores[0].Nome);
        Assert.Equal(250m, result.TopDevedores[0].SaldoDevedor);
        Assert.Equal("Maria Souza", result.TopDevedores[1].Nome);
        Assert.Equal(150m, result.TopDevedores[1].SaldoDevedor);
    }

    [Fact]
    public async Task Handle_DeveIncluirProdutosDeTodosOsTipos_ComEstoqueVasilhameEEmprestado()
    {
        var (db, _, _, _) = await CriarBaseAsync();
        // CriarBaseAsync cria produto1 "Botijão 13kg" (sem controle) e produto2 "Água 20L" (sem controle).
        // Adiciona um produto com controle e vasilhames para exercitar o cálculo das 3 categorias.
        var comControle = new Produto("Botijão 13kg Controla", TipoProduto.Gas, 100m, 60m, 80m, controlaVasilhame: true);
        comControle.RegistrarEntrada(10);      // 10 cheios
        comControle.RegistrarEntradaVasilhame(5); // 5 vazios disponíveis
        comControle.RegistrarEmprestimoVasilhame(3); // 3 emprestados
        db.Produtos.Add(comControle);
        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        // Retorna todos os produtos (inclusive os sem controle).
        Assert.Equal(3, result.Vasilhames.Count);

        var item = Assert.Single(result.Vasilhames, v => v.ProdutoId == comControle.Id);
        Assert.Equal(10, item.Estoque);
        Assert.Equal(5, item.Vasilhame);
        Assert.Equal(3, item.Emprestado);

        var semControle = Assert.Single(result.Vasilhames, v => v.Nome == "Botijão 13kg");
        Assert.Equal(2, semControle.Estoque);
        Assert.Equal(0, semControle.Vasilhame);   // sem controle → zerado
        Assert.Equal(0, semControle.Emprestado);  // sem controle → zerado
    }

    /// <summary>Cria/atualiza a recorrência de um par a partir das vendas, persistindo (como o handler real).</summary>
    private static async Task RecalcularEPersistirAsync(AppDbContext db, Guid clienteId, Guid produtoId)
    {
        await RecorrenciaCompraAprendizado.RecalcularAsync(db, clienteId, produtoId, CancellationToken.None);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Handle_DeveRetornarRecomprasVencidas_NaoRetornarNaoVencidasNemSemIntervalo()
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria Souza", "11999999999", "Rua A, 10");
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m, 5,
            avisarRecompra: true, diasPadraoRecompra: 45);
        produto.RegistrarEntrada(10);
        db.Clientes.Add(cliente);
        db.Produtos.Add(produto);

        // Vencida: comprou 01/08 → dataPrevista 15/09 (45d) → hoje 15/09 → aparece.
        var vendaVencida = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 8, 1, 14, 0, 0, DateTimeKind.Utc));
        vendaVencida.AdicionarItem(produto.Id, 1, 100m, 60m);
        db.Vendas.Add(vendaVencida);

        // Ainda não vencida: comprou 01/09 → dataPrevista 16/10 → hoje 15/09 → NÃO aparece.
        var outro = new Cliente("João Pereira", "11988887777", "Rua B, 20");
        var produtoSemVencido = new Produto("Água 20L", TipoProduto.Agua, 25m, 12m, 18m, 10,
            avisarRecompra: true, diasPadraoRecompra: 45);
        produtoSemVencido.RegistrarEntrada(10);
        db.Clientes.Add(outro);
        db.Produtos.Add(produtoSemVencido);
        var vendaNaoVencida = new Venda(outro.Id, FormaPagamento.Dinheiro, new DateTime(2026, 9, 1, 14, 0, 0, DateTimeKind.Utc));
        vendaNaoVencida.AdicionarItem(produtoSemVencido.Id, 1, 25m, 12m);
        db.Vendas.Add(vendaNaoVencida);

        // Intervalo 0 (DiasPadraoRecompra = 0): NÃO aparece mesmo com 1 compra.
        var semIntervalo = new Cliente("Ana Lima", "11977776666", "Rua C, 30");
        var prodSemIntervalo = new Produto("Copo 500ml", TipoProduto.Outros, 2m, 1m, 1.5m, 50,
            avisarRecompra: true, diasPadraoRecompra: 0);
        prodSemIntervalo.RegistrarEntrada(100);
        db.Clientes.Add(semIntervalo);
        db.Produtos.Add(prodSemIntervalo);
        var vendaSemIntervalo = new Venda(semIntervalo.Id, FormaPagamento.Dinheiro, new DateTime(2026, 7, 1, 14, 0, 0, DateTimeKind.Utc));
        vendaSemIntervalo.AdicionarItem(prodSemIntervalo.Id, 1, 2m, 1m);
        db.Vendas.Add(vendaSemIntervalo);

        await db.SaveChangesAsync();

        // Popula as recorrências (a query lê da tabela aprendida, não das vendas diretamente).
        await RecalcularEPersistirAsync(db, cliente.Id, produto.Id);
        await RecalcularEPersistirAsync(db, outro.Id, produtoSemVencido.Id);
        await RecalcularEPersistirAsync(db, semIntervalo.Id, prodSemIntervalo.Id);

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        var item = Assert.Single(result.Recompras);
        Assert.Equal(cliente.Id, item.ClienteId);
        Assert.Equal("Maria Souza", item.ClienteNome);
        Assert.Equal("11999999999", item.ClienteTelefone);
        Assert.Equal(produto.Id, item.ProdutoId);
        Assert.Equal("Botijão 13kg", item.ProdutoNome);
        Assert.Equal(45m, item.IntervaloMedioDias);
        Assert.Equal(45, item.DiasDecorridos);
    }

    [Fact]
    public async Task Handle_ProdutoInativo_ComRecompra_DeveContinuarRetornando()
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria Souza", "11999999999", "Rua A, 10");
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m, 5,
            avisarRecompra: true, diasPadraoRecompra: 45);
        produto.RegistrarEntrada(10);
        produto.Desativar(); // inativo: o operador liga para oferecer outro produto
        db.Clientes.Add(cliente);
        db.Produtos.Add(produto);

        var venda = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 8, 1, 14, 0, 0, DateTimeKind.Utc));
        venda.AdicionarItem(produto.Id, 1, 100m, 60m);
        db.Vendas.Add(venda);
        await db.SaveChangesAsync();
        await RecalcularEPersistirAsync(db, cliente.Id, produto.Id);

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        Assert.Contains(result.Recompras, r => r.ProdutoId == produto.Id);
    }

    [Fact]
    public async Task Handle_RecompraVencida_MasForaDaJanela_NaoDeveRetornar()
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria Souza", "11999999999", "Rua A, 10");
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m, 5,
            avisarRecompra: true, diasPadraoRecompra: 45);
        produto.RegistrarEntrada(10);
        db.Clientes.Add(cliente);
        db.Produtos.Add(produto);

        // 2 vendas com gap 45; última compra em 15/04 (5 meses atrás). Intervalo 45 →
        // dataPrevista 30/05 (vencida) e janela até dataPrevista + 2×45 = 28/08. Hoje 15/09
        // está DEPOIS da janela → o cliente "desistiu" e não deve retornar.
        var venda1 = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 3, 1, 14, 0, 0, DateTimeKind.Utc));
        venda1.AdicionarItem(produto.Id, 1, 100m, 60m);
        var venda2 = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 4, 15, 14, 0, 0, DateTimeKind.Utc)); // gap 45
        venda2.AdicionarItem(produto.Id, 1, 100m, 60m);
        db.Vendas.AddRange(venda1, venda2);
        await db.SaveChangesAsync();
        await RecalcularEPersistirAsync(db, cliente.Id, produto.Id);

        var handler = CriarHandler(db, FixedNow); // hoje 15/09; última compra 15/04 → fora da janela
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        Assert.DoesNotContain(result.Recompras, r => r.ClienteId == cliente.Id);
    }

    [Fact]
    public async Task Handle_RecompraComIntervaloZero_ComMultiplasComprasMesmoDia_DeveIgnorar()
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria Souza", "11999999999", "Rua A, 10");
        // AvisarRecompra com DiasPadraoRecompra 0 e 2 vendas no mesmo dia (gap 0 < 7 → ignorado).
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m, 5,
            avisarRecompra: true, diasPadraoRecompra: 0);
        produto.RegistrarEntrada(20);
        db.Clientes.Add(cliente);
        db.Produtos.Add(produto);

        var venda1 = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 8, 1, 14, 0, 0, DateTimeKind.Utc));
        venda1.AdicionarItem(produto.Id, 1, 100m, 60m);
        var venda2 = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 8, 1, 15, 0, 0, DateTimeKind.Utc)); // mesmo dia
        venda2.AdicionarItem(produto.Id, 1, 100m, 60m);
        db.Vendas.AddRange(venda1, venda2);
        await db.SaveChangesAsync();
        await RecalcularEPersistirAsync(db, cliente.Id, produto.Id);

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        // IntervaloMedioDias ficou 0 (sem gap válido e default 0) → não aparece.
        Assert.DoesNotContain(result.Recompras, r => r.ClienteId == cliente.Id);
    }

    [Fact]
    public async Task Handle_RecomprasVencidas_DeveOrdenarMaisAtrasadoPrimeiro()
    {
        // Dois clientes vencidos no mesmo produto, com atrasos diferentes — o mais atrasado
        // (DataPrevista mais antiga) deve vir antes na lista (a spec: "mais atrasado primeiro").
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var atrasado = new Cliente("Carlos Antigo", "11966665555", "Rua D, 40");
        var menosAtrasado = new Cliente("Bia Recente", "11955554444", "Rua E, 50");
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m, 5,
            avisarRecompra: true, diasPadraoRecompra: 45);
        produto.RegistrarEntrada(10);
        db.Clientes.AddRange(atrasado, menosAtrasado);
        db.Produtos.Add(produto);

        // Carlos comprou 28/07 → dataPrevista 11/09 (4 dias de atraso em 15/09).
        // Bia comprou 25/07 → dataPrevista 08/09 (7 dias de atraso em 15/09) — a MAIS atrasada.
        var vendaCarlos = new Venda(atrasado.Id, FormaPagamento.Dinheiro, new DateTime(2026, 7, 28, 14, 0, 0, DateTimeKind.Utc));
        vendaCarlos.AdicionarItem(produto.Id, 1, 100m, 60m);
        var vendaBia = new Venda(menosAtrasado.Id, FormaPagamento.Dinheiro, new DateTime(2026, 7, 25, 14, 0, 0, DateTimeKind.Utc));
        vendaBia.AdicionarItem(produto.Id, 1, 100m, 60m);
        db.Vendas.AddRange(vendaCarlos, vendaBia);
        await db.SaveChangesAsync();
        await RecalcularEPersistirAsync(db, atrasado.Id, produto.Id);
        await RecalcularEPersistirAsync(db, menosAtrasado.Id, produto.Id);

        var handler = CriarHandler(db, FixedNow); // hoje 15/09
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        // "Mais atrasado primeiro" → Bia (DataPrevista mais antiga) antes de Carlos.
        var nomes = result.Recompras.Select(r => r.ClienteNome).ToList();
        Assert.Equal(["Bia Recente", "Carlos Antigo"], nomes);
    }

    [Fact]
    public async Task Handle_ProdutoComAvisoDesligado_NaoDeveRetornarRecompra()
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria Souza", "11999999999", "Rua A, 10");
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m, 5,
            avisarRecompra: true, diasPadraoRecompra: 45);
        produto.RegistrarEntrada(10);
        db.Clientes.Add(cliente);
        db.Produtos.Add(produto);
        await db.SaveChangesAsync();

        // Venda vencida (comprou 01/08 → dataPrevista 15/09 → hoje 15/09 aparece) e aprendizado.
        var venda = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 8, 1, 14, 0, 0, DateTimeKind.Utc));
        venda.AdicionarItem(produto.Id, 1, 100m, 60m);
        db.Vendas.Add(venda);
        await db.SaveChangesAsync();
        await RecalcularEPersistirAsync(db, cliente.Id, produto.Id);

        // Admin desliga o aviso de recompra do produto (flag → false) sem tocar nas vendas.
        produto.AtualizarDados(produto.Nome, produto.Tipo, produto.PrecoVenda, produto.PrecoCusto, produto.PrecoGasDoPovo,
            produto.EstoqueMinimo, produto.ControlaVasilhame, avisarRecompra: false, produto.DiasPadraoRecompra);
        await db.SaveChangesAsync();

        var handler = CriarHandler(db, FixedNow);
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        Assert.DoesNotContain(result.Recompras, r => r.ClienteId == cliente.Id && r.ProdutoId == produto.Id);
    }

    [Fact]
    public async Task Handle_AvisoDeRecompra_DeveUsarDiaCivilDeBrasilia_NaoMeiaNoiteUtc()
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria Souza", "11999999999", "Rua A, 10");
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m, 5,
            avisarRecompra: true, diasPadraoRecompra: 30);
        produto.RegistrarEntrada(10);
        db.Clientes.Add(cliente);
        db.Produtos.Add(produto);

        // Compra às 21:30 BRT de 15/08 (00:30 UTC de 16/08). Hoje 14/09 20:30 BRT (23:30 UTC):
        // no calendário local já se passou o intervalo (15/08 → 14/09 = 30 dias) → aviso aparece.
        // A referência correta é o dia civil de Brasília, não a meia-noite UTC (que ainda é 15/09).
        var venda = new Venda(cliente.Id, FormaPagamento.Dinheiro, new DateTime(2026, 8, 16, 0, 30, 0, DateTimeKind.Utc));
        venda.AdicionarItem(produto.Id, 1, 100m, 60m);
        db.Vendas.Add(venda);
        await db.SaveChangesAsync();
        await RecalcularEPersistirAsync(db, cliente.Id, produto.Id);

        var handler = CriarHandler(db, new DateTime(2026, 9, 14, 23, 30, 0, DateTimeKind.Utc));
        var result = await handler.Handle(new ObterDashboardQuery(PeriodoDashboard.Hoje), CancellationToken.None);

        Assert.Contains(result.Recompras, r => r.ClienteId == cliente.Id && r.ProdutoId == produto.Id);
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private static ObterDashboardQueryHandler CriarHandler(AppDbContext db, DateTime utcNow) =>
        new(db, TenantTeste.Contexto(), new FixedTimeProvider(utcNow));
}