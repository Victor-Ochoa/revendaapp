using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Common.Relatorios;
using RevendaApp.Web.Features.Relatorios.Estoque;

namespace RevendaApp.UnitTests.Features.Relatorios;

public class ObterRelatorioEstoqueQueryHandlerTests
{
    /// <summary>Cria o contexto com um produto e devolve ambos; o teste adiciona movimentos com o Id dele.</summary>
    private static async Task<(RevendaApp.Infrastructure.AppDbContext Db, Produto Produto)> CenarioAsync()
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m);
        produto.RegistrarEntrada(50);
        db.Produtos.Add(produto);
        await db.SaveChangesAsync();
        return (db, produto);
    }

    [Fact]
    public async Task Estoque_agrupa_entradas_e_saidas_por_dia_de_brasilia()
    {
        // A movimentação PRECISA usar o Id do produto — um Id aleatório ficaria órfão e o handler
        // (que lista produtos e casa movimento por ProdutoId) a descartaria, deixando o teste
        // verde por vacuidade.
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m);
        produto.RegistrarEntrada(50);
        db.Produtos.Add(produto);
        // 02/11 02:00 UTC = 01/11 23:00 BRT → conta no dia 1.
        db.MovimentacoesEstoque.Add(new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Saida, 3, "Venda",
            new DateTime(2026, 11, 2, 2, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        var handler = new ObterRelatorioEstoqueQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioEstoqueQuery(new PeriodoMes(2026, 11)), CancellationToken.None);

        var linha = resultado.Produtos.Single();
        Assert.Equal(3, linha.SaidasPorDia[0]);   // índice 0 = dia 1
        Assert.Equal(0, linha.SaidasPorDia[1]);
    }

    [Fact]
    public async Task Estoque_exclui_ajustes_da_matriz_e_lista_em_ajustes()
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m);
        produto.RegistrarEntrada(50);
        db.Produtos.Add(produto);
        db.MovimentacoesEstoque.AddRange(
            new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Ajuste, 42, "Inventário",
                new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc)),
            new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Entrada, 5, "Compra",
                new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        var handler = new ObterRelatorioEstoqueQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioEstoqueQuery(new PeriodoMes(2026, 11)), CancellationToken.None);

        var linha = resultado.Produtos.Single();
        Assert.Equal(5, linha.TotalEntradas);
        Assert.Equal(0, linha.TotalSaidas);
        Assert.Equal(1, linha.QuantidadeAjustes);
        var ajuste = Assert.Single(resultado.Ajustes);
        Assert.Equal(10, ajuste.Dia);
        Assert.Equal(42, ajuste.NovoTotal);
    }

    [Fact]
    public async Task Estoque_calcula_totais_liquido_e_estoque_atual()
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m);
        produto.RegistrarEntrada(50);
        db.Produtos.Add(produto);
        db.MovimentacoesEstoque.AddRange(
            new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Entrada, 10, "Compra",
                new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc)),
            new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Saida, 4, "Venda",
                new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        var handler = new ObterRelatorioEstoqueQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioEstoqueQuery(new PeriodoMes(2026, 11)), CancellationToken.None);

        var linha = resultado.Produtos.Single();
        Assert.Equal(10, linha.TotalEntradas);
        Assert.Equal(4, linha.TotalSaidas);
        Assert.Equal(6, linha.MovimentoLiquido);
        Assert.Equal(50, linha.EstoqueAtual);   // estado de hoje, não do mês
    }

    [Fact]
    public async Task Estoque_inclui_inativo_sem_movimento_com_vetores_zerados()
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var produto = new Produto("Desativado", TipoProduto.Agua, 10m, 5m, 8m);
        produto.Desativar();
        db.Produtos.Add(produto);
        await db.SaveChangesAsync();
        var handler = new ObterRelatorioEstoqueQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioEstoqueQuery(new PeriodoMes(2026, 11)), CancellationToken.None);

        var linha = Assert.Single(resultado.Produtos);
        Assert.False(linha.Ativo);
        Assert.All(linha.EntradasPorDia, v => Assert.Equal(0, v));
        Assert.Equal(0, linha.MovimentoLiquido);
    }

    [Fact]
    public async Task Estoque_mes_sem_movimento_retorna_matriz_do_tamanho_do_mes()
    {
        var (db, _) = await CenarioAsync();
        var handler = new ObterRelatorioEstoqueQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioEstoqueQuery(new PeriodoMes(2026, 11)), CancellationToken.None);

        var linha = resultado.Produtos.Single();
        Assert.Equal(30, linha.EntradasPorDia.Count);
        Assert.Equal(30, linha.SaidasPorDia.Count);
        Assert.Equal(30, resultado.PorDia.Count);
        Assert.Empty(resultado.Ajustes);
    }

    [Fact]
    public async Task Estoque_nao_inclui_movimento_de_fora_do_mes()
    {
        // 31/10 12:00 UTC e 01/12 12:00 UTC ficam fora de novembro.
        var (db, produto) = await CenarioAsync();
        db.MovimentacoesEstoque.AddRange(
            new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Entrada, 7, "Compra",
                new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc)),
            new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Entrada, 9, "Compra",
                new DateTime(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        var handler = new ObterRelatorioEstoqueQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioEstoqueQuery(new PeriodoMes(2026, 11)), CancellationToken.None);

        Assert.Equal(0, resultado.Produtos.Single().TotalEntradas);
    }

    [Fact]
    public async Task Estoque_soma_por_dia_entre_produtos()
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var gas = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m);
        var agua = new Produto("Água 20L", TipoProduto.Agua, 20m, 10m, 15m);
        db.Produtos.AddRange(gas, agua);
        db.MovimentacoesEstoque.AddRange(
            new MovimentacaoEstoque(gas.Id, TipoMovimentacaoEstoque.Saida, 2, "Venda",
                new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc)),
            new MovimentacaoEstoque(agua.Id, TipoMovimentacaoEstoque.Saida, 5, "Venda",
                new DateTime(2026, 11, 3, 14, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        var handler = new ObterRelatorioEstoqueQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioEstoqueQuery(new PeriodoMes(2026, 11)), CancellationToken.None);

        var dia3 = resultado.PorDia.Single(d => d.Dia == 3);
        Assert.Equal(7, dia3.Saidas);
        Assert.Equal(30, resultado.PorDia.Count);
    }

    /// <summary>
    /// O estorno de venda editada/excluída é uma Entrada, mas NÃO é compra: somá-lo infla o total
    /// comprado da distribuidora. Este é o caso que motivou a coluna Origem — filtrar por
    /// <c>Tipo == Entrada</c> daria 60 aqui, quando a resposta certa é 50.
    /// </summary>
    [Fact]
    public async Task Estoque_exclui_estorno_de_venda_das_entradas()
    {
        var (db, produto) = await CenarioAsync();
        db.MovimentacoesEstoque.AddRange(
            new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Entrada, 50,
                data: new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc),
                origem: OrigemMovimentacaoEstoque.Compra),
            new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Entrada, 10,
                data: new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc),
                origem: OrigemMovimentacaoEstoque.EstornoVenda));
        await db.SaveChangesAsync();
        var handler = new ObterRelatorioEstoqueQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(
            new ObterRelatorioEstoqueQuery(new PeriodoMes(2026, 11)), CancellationToken.None);

        var linha = resultado.Produtos.Single();
        Assert.Equal(50, linha.TotalEntradas);
        Assert.Equal(0, linha.EntradasPorDia[5]);   // dia 6 não conta o estorno
        Assert.Equal(50, linha.EntradasPorDia[4]);  // dia 5 conta a compra
    }

    /// <summary>
    /// Filtro "Entradas": a saída de venda some da matriz e do total, mas o ajuste continua listado
    /// (ajuste não é movimento — é um novo total).
    /// </summary>
    [Fact]
    public async Task Estoque_filtro_entradas_nao_conta_saidas()
    {
        var (db, produto) = await CenarioAsync();
        db.MovimentacoesEstoque.AddRange(
            new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Entrada, 20,
                data: new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc),
                origem: OrigemMovimentacaoEstoque.Compra),
            new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Saida, 7,
                data: new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc),
                origem: OrigemMovimentacaoEstoque.Venda),
            new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Ajuste, 33, "Inventário",
                new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        var handler = new ObterRelatorioEstoqueQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(
            new ObterRelatorioEstoqueQuery(new PeriodoMes(2026, 11), FiltroMovimentoEstoque.Entradas),
            CancellationToken.None);

        var linha = resultado.Produtos.Single();
        Assert.Equal(20, linha.TotalEntradas);
        Assert.Equal(0, linha.TotalSaidas);
        Assert.All(linha.SaidasPorDia, v => Assert.Equal(0, v));
        Assert.Single(resultado.Ajustes);
        Assert.Equal(0, resultado.PorDia.Sum(d => d.Saidas));
    }

    /// <summary>Filtro "Saídas": a compra some, a saída de venda fica.</summary>
    [Fact]
    public async Task Estoque_filtro_saidas_nao_conta_entradas()
    {
        var (db, produto) = await CenarioAsync();
        db.MovimentacoesEstoque.AddRange(
            new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Entrada, 20,
                data: new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc),
                origem: OrigemMovimentacaoEstoque.Compra),
            new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Saida, 7,
                data: new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc),
                origem: OrigemMovimentacaoEstoque.Venda));
        await db.SaveChangesAsync();
        var handler = new ObterRelatorioEstoqueQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(
            new ObterRelatorioEstoqueQuery(new PeriodoMes(2026, 11), FiltroMovimentoEstoque.Saidas),
            CancellationToken.None);

        var linha = resultado.Produtos.Single();
        Assert.Equal(0, linha.TotalEntradas);
        Assert.All(linha.EntradasPorDia, v => Assert.Equal(0, v));
        Assert.Equal(7, linha.TotalSaidas);
        Assert.Equal(0, resultado.PorDia.Sum(d => d.Entradas));
    }

    /// <summary>
    /// O default da query é "Ambos": nenhum chamador existente (nem a tela antes do filtro) muda de
    /// comportamento ao adicionar o parâmetro.
    /// </summary>
    [Fact]
    public async Task Estoque_filtro_padrao_e_ambos()
    {
        var (db, produto) = await CenarioAsync();
        db.MovimentacoesEstoque.AddRange(
            new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Entrada, 20,
                data: new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc),
                origem: OrigemMovimentacaoEstoque.Compra),
            new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Saida, 7,
                data: new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc),
                origem: OrigemMovimentacaoEstoque.Venda));
        await db.SaveChangesAsync();
        var handler = new ObterRelatorioEstoqueQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(
            new ObterRelatorioEstoqueQuery(new PeriodoMes(2026, 11)), CancellationToken.None);

        var linha = resultado.Produtos.Single();
        Assert.Equal(20, linha.TotalEntradas);
        Assert.Equal(7, linha.TotalSaidas);
        Assert.Equal(13, linha.MovimentoLiquido);
    }

    [Fact]
    public async Task Estoque_sem_empresa_selecionada_lanca()
    {
        var tenant = new FakeTenantContext(empresaId: null, ehSuperAdmin: true);
        await using var db = InMemoryDbContextFactory.Create(tenant: tenant);
        var handler = new ObterRelatorioEstoqueQueryHandler(db, tenant);

        var excecao = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(new ObterRelatorioEstoqueQuery(new PeriodoMes(2026, 11)), CancellationToken.None).AsTask());

        Assert.Contains("exige uma empresa", excecao.Message);
    }
}