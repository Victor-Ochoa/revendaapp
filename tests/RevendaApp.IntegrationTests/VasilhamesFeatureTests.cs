using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Enums;
using RevendaApp.Infrastructure;
using RevendaApp.IntegrationTests.TestHelpers;
using RevendaApp.Web.Features.Admin.Vendas;
using RevendaApp.Web.Features.Clientes.Emprestimos;
using RevendaApp.Web.Features.Estoque.Cadastrar;
using RevendaApp.Web.Features.Estoque.Movimentar;
using RevendaApp.Web.Features.Estoque.Vasilhames;
using RevendaApp.Web.Features.Vendas.NovaVenda;

namespace RevendaApp.IntegrationTests.Tests;

/// <summary>
/// Testes de integração do controle de vasilhames contra um Postgres real (via
/// <see cref="DatabaseFixture"/>): venda em empréstimo aumenta os emprestados e cria empréstimo
/// aberto; devolução abate os emprestados e devolve o vazio ao disponível; compra entrega vazios
/// ao fornecedor; estorno de venda de empréstimo reverte emprestados e remove as linhas abertas da
/// venda; e a tela "para quem" agrega o cenário.
/// </summary>
public class VasilhamesFeatureTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Cria um produto que controla vasilhame, com estoque cheio e vazios disponíveis.</summary>
    private static async Task<Guid> CriarProdutoComVasilhameAsync(AppDbContext db, int estoque = 20)
    {
        var produtoId = await new CadastrarProdutoCommandHandler(db, TenantIntegracao.Contexto())
            .Handle(new CadastrarProdutoCommand("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m, 5, ControlaVasilhame: true), CancellationToken.None);

        // A entrada (compra) de controle CONSUME vazios (entrega ao fornecedor): para montar o
        // estoque inicial, primeiro adiciona vazios suficientes no depósito e depois entra com os
        // cheios (que consomem esses vazios).
        var produto = await db.Produtos.FindAsync([produtoId]);
        produto!.RegistrarEntradaVasilhame(estoque);
        await db.SaveChangesAsync();

        await new RegistrarMovimentacaoEstoqueCommandHandler(db, TenantIntegracao.Contexto())
            .Handle(new RegistrarMovimentacaoEstoqueCommand(produtoId, TipoMovimentacaoEstoque.Entrada, estoque, "Estoque inicial"), CancellationToken.None);
        return produtoId;
    }

    /// <summary>Registra uma venda em empréstimo (item <c>EhEmprestimo: true</c>) para um cliente novo.</summary>
    private static async Task<RegistrarVendaResult> RegistrarVendaEmprestimoAsync(
        AppDbContext db, Guid produtoId, int quantidade, FormaPagamento formaPagamento = FormaPagamento.Dinheiro)
    {
        return await new RegistrarVendaCommandHandler(db, TenantIntegracao.Contexto()).Handle(
            new RegistrarVendaCommand(null, "Maria Souza", "11988887777", "Rua A, 1", formaPagamento, 0m,
                [new ItemVendaInput(produtoId, quantidade, EhEmprestimo: true)]),
            CancellationToken.None);
    }

    [Fact]
    public async Task VendaEmprestimo_DeveAumentarEmprestados_ECriarEmprestimoAberto()
    {
        using var db = fixture.CreateDbContext();
        var produtoId = await CriarProdutoComVasilhameAsync(db, estoque: 20);

        var venda = await RegistrarVendaEmprestimoAsync(db, produtoId, 3);

        var produto = await db.Produtos.FindAsync([produtoId]);

        // O vasilhame vazio ficou com o cliente (Emprestados +3) e o cheio saiu do estoque.
        Assert.Equal(3, produto!.QuantidadeVasilhamesEmprestados);
        Assert.Equal(17, produto.QuantidadeEstoque); // 20 - 3 (RegistrarSaidaEmprestimo)

        // Um empréstimo aberto foi criado, com saldo = quantidade emprestada.
        var emprestimo = Assert.Single(await db.EmprestimosVasilhames.Where(e => e.ClienteId == venda.ClienteId && e.ProdutoId == produtoId).ToListAsync());
        Assert.Equal(3, emprestimo.Saldo);
        Assert.Equal(0, emprestimo.QuantidadeDevolvida);
        Assert.Null(emprestimo.DevolvidoEm);
    }

    [Fact]
    public async Task VendaEmprestimo_DeveUsarEstoqueDoCliente_QuandoEstoqueCheioInsuficiente()
    {
        using var db = fixture.CreateDbContext();
        var produtoId = await CriarProdutoComVasilhameAsync(db, estoque: 20);

        // 1ª venda: 10 de empréstimo para a cliente Maria (abre 10 emprestados).
        var venda = await RegistrarVendaEmprestimoAsync(db, produtoId, 10);

        // 2ª venda: a mesma clienta pede mais 15 — o Validator permite porque
        // Estoque (10) + Emprestados da clienta (10) >= 15, e o Estoque pode ficar negativo.
        // Chamamos o handler Diretamente (sem Validator) para exercitar a regra de negócio real.
        var resultado2 = await new RegistrarVendaCommandHandler(db, TenantIntegracao.Contexto()).Handle(
            new RegistrarVendaCommand(venda.ClienteId, null, null, null, FormaPagamento.Dinheiro, 0m,
                [new ItemVendaInput(produtoId, 15, EhEmprestimo: true)]),
            CancellationToken.None);

        var produto = await db.Produtos.FindAsync([produtoId]);
        Assert.NotNull(resultado2);
        Assert.Equal(25, produto!.QuantidadeVasilhamesEmprestados); // 10 + 15
        Assert.Equal(-5, produto.QuantidadeEstoque); // 20 - 10 - 15 (débito coberto por emprestados)
    }

    [Fact]
    public async Task Devolucao_DeveAbaterEmprestados_E_DevolverVaziosAoDisponivel()
    {
        using var db = fixture.CreateDbContext();
        var produtoId = await CriarProdutoComVasilhameAsync(db, estoque: 20);

        var venda = await RegistrarVendaEmprestimoAsync(db, produtoId, 5);
        var clienteId = venda.ClienteId;

        await new RegistrarDevolucaoVasilhameCommandHandler(db, TenantIntegracao.Contexto()).Handle(
            new RegistrarDevolucaoVasilhameCommand(clienteId, produtoId, 2, "Devolução manual"),
            CancellationToken.None);

        var produto = await db.Produtos.FindAsync([produtoId]);
        Assert.Equal(3, produto!.QuantidadeVasilhamesEmprestados); // 5 - 2
        Assert.Equal(15, produto.QuantidadeEstoque); // 20 - 5 (venda) — Estoque intocado pela devolução
        Assert.Equal(2, produto.QuantidadeVasilhames); // 2 vazios voltam ao disponível

        // O empréstimo continua aberto com saldo 3 (2 devolvidos de 5).
        var emprestimo = await db.EmprestimosVasilhames.SingleAsync(e => e.ClienteId == clienteId && e.ProdutoId == produtoId);
        Assert.Equal(2, emprestimo.QuantidadeDevolvida);
        Assert.Equal(3, emprestimo.Saldo);
        Assert.Null(emprestimo.DevolvidoEm);
    }

    [Fact]
    public async Task Devolucao_SemEstoqueCheio_DeveAbaterEmprestados_E_DevolverVaziosAoDisponivel()
    {
        using var db = fixture.CreateDbContext();
        var produtoId = await CriarProdutoComVasilhameAsync(db, estoque: 4);

        var venda = await RegistrarVendaEmprestimoAsync(db, produtoId, 4);
        var clienteId = venda.ClienteId;

        await new RegistrarDevolucaoVasilhameCommandHandler(db, TenantIntegracao.Contexto()).Handle(
            new RegistrarDevolucaoVasilhameCommand(clienteId, produtoId, 2, "Devolução em estoque vazio"),
            CancellationToken.None);

        var produto = await db.Produtos.FindAsync([produtoId]);
        Assert.Equal(2, produto!.QuantidadeVasilhamesEmprestados); // 4 - 2
        Assert.Equal(0, produto.QuantidadeEstoque); // 4 - 4 (venda) — Estoque intocado
        Assert.Equal(2, produto.QuantidadeVasilhames); // 2 vazios voltam ao disponível
    }

    [Fact]
    public async Task Devolucao_Total_DeveQuitarEmprestimo_EDefinirDevolvidoEm()
    {
        using var db = fixture.CreateDbContext();
        var produtoId = await CriarProdutoComVasilhameAsync(db, estoque: 20);

        var venda = await RegistrarVendaEmprestimoAsync(db, produtoId, 3);
        var clienteId = venda.ClienteId;

        await new RegistrarDevolucaoVasilhameCommandHandler(db, TenantIntegracao.Contexto()).Handle(
            new RegistrarDevolucaoVasilhameCommand(clienteId, produtoId, 3, "Quitação total"),
            CancellationToken.None);

        var emprestimo = await db.EmprestimosVasilhames.SingleAsync(e => e.ClienteId == clienteId && e.ProdutoId == produtoId);
        Assert.Equal(0, emprestimo.Saldo);
        Assert.NotNull(emprestimo.DevolvidoEm);
        Assert.Equal(0, (await db.Produtos.FindAsync([produtoId]))!.QuantidadeVasilhamesEmprestados);
    }

    [Fact]
    public async Task EstornarVendaEmprestimo_DeveReverterEmprestados_ERemoverLinhasAbertas()
    {
        using var db = fixture.CreateDbContext();
        var produtoId = await CriarProdutoComVasilhameAsync(db, estoque: 20);

        var venda = await RegistrarVendaEmprestimoAsync(db, produtoId, 4);

        // Estorno via ExcluirVendaCommandHandler → ReversaoVendaHelper.ReverterAsync.
        await new ExcluirVendaCommandHandler(db, TenantIntegracao.Contexto()).Handle(new ExcluirVendaCommand(venda.VendaId), CancellationToken.None);

        var produto = await db.Produtos.FindAsync([produtoId]);
        Assert.Equal(0, produto!.QuantidadeVasilhamesEmprestados); // reverteram o vasilhame
        Assert.Equal(20, produto.QuantidadeEstoque); // estoque cheio restaurado

        // As linhas abertas do empréstimo da venda foram removidas.
        Assert.Empty(await db.EmprestimosVasilhames
            .Where(e => e.ClienteId == venda.ClienteId && e.ProdutoId == produtoId && e.DevolvidoEm == null)
            .ToListAsync());
    }

    [Fact]
    public async Task ObterControleVasilhames_DeveAgregarDisponivelEmprestadosTotal_EParaQuem()
    {
        using var db = fixture.CreateDbContext();
        var produtoId = await CriarProdutoComVasilhameAsync(db, estoque: 20);

        // Ajusta os vazios pela API do domínio (entidade rastreada): um ExecuteUpdate aqui alteraria
        // a linha por fora do change tracker e o `xmin` da entidade ficaria defasado, fazendo a
        // venda seguinte falhar com conflito de concorrência legítimo.
        var produtoPreparo = await db.Produtos.FindAsync([produtoId]);
        produtoPreparo!.AjustarVasilhames(10);
        await db.SaveChangesAsync();

        // Maria pega 3 em empréstimo; João pega 2 (clientes abertos distintos).
        var vendaMaria = await RegistrarVendaEmprestimoAsync(db, produtoId, 3);
        var clienteMaria = vendaMaria.ClienteId;
        var vendaJoao = await new RegistrarVendaCommandHandler(db, TenantIntegracao.Contexto()).Handle(
            new RegistrarVendaCommand(null, "João Silva", "11955556666", "Rua B, 2", FormaPagamento.Dinheiro, 0m,
                [new ItemVendaInput(produtoId, 2, EhEmprestimo: true)]),
            CancellationToken.None);
        var clienteJoao = vendaJoao.ClienteId;

        var controle = await new ObterControleVasilhamesQueryHandler(db, TenantIntegracao.Contexto())
            .Handle(new ObterControleVasilhamesQuery(), CancellationToken.None);

        var item = Assert.Single(controle);
        Assert.Equal(produtoId, item.ProdutoId);
        Assert.Equal(10, item.Disponivel);   // QuantidadeVasilhames
        Assert.Equal(5, item.Emprestados);   // 3 + 2
        Assert.Equal(15, item.Total);        // 10 + 5

        // Agrupados por cliente ("para quem"), na ordem de maior quantidade.
        Assert.Equal(2, item.EmprestimosPara.Count);
        Assert.Equal(3, item.EmprestimosPara[0].Quantidade);
        Assert.Equal(clienteMaria, item.EmprestimosPara[0].ClienteId);
        Assert.Equal(2, item.EmprestimosPara[1].Quantidade);
        Assert.Equal(clienteJoao, item.EmprestimosPara[1].ClienteId);
    }
}