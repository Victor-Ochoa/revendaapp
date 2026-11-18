using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.IntegrationTests.TestHelpers;
using RevendaApp.Web.Common.Relatorios;
using RevendaApp.Web.Features.Relatorios.Estoque;
using RevendaApp.Web.Features.Relatorios.Fiado;
using RevendaApp.Web.Features.Relatorios.Vasilhames;
using RevendaApp.Web.Features.Relatorios.Vendas;

namespace RevendaApp.IntegrationTests.Tests;

/// <summary>
/// Prova que os relatórios respeitam o isolamento por empresa, contra o Postgres real, com duas
/// empresas de verdade.
/// </summary>
/// <remarks>
/// <para>
/// Cada teste tem <b>controle positivo</b> (o dado da própria empresa aparece) antes do assert de
/// ausência na outra empresa. Sem o controle, um relatório quebrado que devolve sempre vazio
/// passaria no teste de isolamento — vacuidade clássica.
/// </para>
/// <para>
/// Os dados de domínio são gravados por <c>CreateDbContext(empresaId)</c>, que instala o
/// <c>SetEmpresaIdInterceptor</c> com o tenant da empresa dona; os handlers rodam com um
/// <c>FakeTenantContext</c> da mesma empresa. A visão central só serve para conferir a sanidade do
/// cenário (as linhas existem de fato).
/// </para>
/// </remarks>
public class RelatoriosIsolationTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly PeriodoMes Novembro2026 = new(2026, 11);

    private Guid _empresaA;
    private Guid _empresaB;

    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();

        await using var db = fixture.CreateDbContextCentral();
        var empresaA = await EmpresaTestHelper.CriarEmpresaAsync(db, "Empresa A Relatórios");
        var empresaB = await EmpresaTestHelper.CriarEmpresaAsync(db, "Empresa B Relatórios");

        _empresaA = empresaA.Id;
        _empresaB = empresaB.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Cria cliente + produto (com controle de vasilhame) numa empresa e devolve os ids.
    /// </summary>
    private async Task<(Guid ClienteId, Guid ProdutoId)> CriarBaseAsync(Guid empresaId, string sufixo)
    {
        await using var db = fixture.CreateDbContext(empresaId);

        // Telefone é chave natural única dentro da empresa: um valor fixo por sufixo basta, já que
        // cada empresa recebe um único cliente por teste.
        var telefone = sufixo == "A" ? "11911110001" : "11922220002";
        var cliente = new Cliente($"Cliente {sufixo}", telefone, "Rua A, 1");
        db.Clientes.Add(cliente);

        var produto = new Produto($"Botijão {sufixo}", TipoProduto.Gas, 100m, 60m, 80m, controlaVasilhame: true);
        produto.RegistrarEntrada(100);
        db.Produtos.Add(produto);

        await db.SaveChangesAsync();
        return (cliente.Id, produto.Id);
    }

    [Fact]
    public async Task RelatorioEstoque_de_A_nao_mostra_movimento_de_B()
    {
        var (_, produtoA) = await CriarBaseAsync(_empresaA, "A");
        var (_, produtoB) = await CriarBaseAsync(_empresaB, "B");

        await using (var dbA = fixture.CreateDbContext(_empresaA))
        {
            dbA.MovimentacoesEstoque.Add(new MovimentacaoEstoque(produtoA, TipoMovimentacaoEstoque.Entrada, 10,
                "Compra A", new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc)));
            await dbA.SaveChangesAsync();
        }

        await using (var dbB = fixture.CreateDbContext(_empresaB))
        {
            dbB.MovimentacoesEstoque.Add(new MovimentacaoEstoque(produtoB, TipoMovimentacaoEstoque.Entrada, 7,
                "Compra B", new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc)));
            await dbB.SaveChangesAsync();
        }

        // Controle positivo: A vê o próprio movimento.
        await using (var dbA = fixture.CreateDbContext(_empresaA))
        {
            var resultado = await new ObterRelatorioEstoqueQueryHandler(dbA, new FakeTenantContext(_empresaA))
                .Handle(new ObterRelatorioEstoqueQuery(Novembro2026), CancellationToken.None);

            Assert.Equal(10, resultado.Produtos.Single().TotalEntradas);
        }

        // Isolamento: A não vê o movimento de B (7).
        await using (var dbA = fixture.CreateDbContext(_empresaA))
        {
            var resultado = await new ObterRelatorioEstoqueQueryHandler(dbA, new FakeTenantContext(_empresaA))
                .Handle(new ObterRelatorioEstoqueQuery(Novembro2026), CancellationToken.None);

            Assert.Equal(10, resultado.Produtos.Sum(p => p.TotalEntradas));
            Assert.DoesNotContain(resultado.Produtos, p => p.ProdutoId == produtoB);
        }

        // Sanidade do cenário: as duas movimentações existem de fato no banco.
        await using var central = fixture.CreateDbContextCentral();
        Assert.Equal(2, await central.MovimentacoesEstoque.CountAsync());
    }

    [Fact]
    public async Task RelatorioVendas_de_A_nao_mostra_venda_de_B()
    {
        var (clienteA, produtoA) = await CriarBaseAsync(_empresaA, "A");
        var (clienteB, produtoB) = await CriarBaseAsync(_empresaB, "B");

        await using (var dbA = fixture.CreateDbContext(_empresaA))
        {
            var venda = new Venda(clienteA, FormaPagamento.Pix, new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc));
            venda.AdicionarItem(produtoA, 1, 100m, 60m);
            dbA.Vendas.Add(venda);
            await dbA.SaveChangesAsync();
        }

        await using (var dbB = fixture.CreateDbContext(_empresaB))
        {
            var venda = new Venda(clienteB, FormaPagamento.Pix, new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc));
            venda.AdicionarItem(produtoB, 1, 50m, 30m);
            dbB.Vendas.Add(venda);
            await dbB.SaveChangesAsync();
        }

        await using var dbConsulta = fixture.CreateDbContext(_empresaA);
        var resultado = await new ObterRelatorioVendasQueryHandler(dbConsulta, new FakeTenantContext(_empresaA))
            .Handle(new ObterRelatorioVendasQuery(Novembro2026), CancellationToken.None);

        // Controle positivo (100) + isolamento (não soma os 50 de B).
        Assert.Equal(1, resultado.QuantidadeVendas);
        Assert.Equal(100m, resultado.FaturamentoTotal);
    }

    [Fact]
    public async Task RelatorioFiado_de_A_nao_lista_cliente_de_B()
    {
        var (clienteA, produtoA) = await CriarBaseAsync(_empresaA, "A");
        var (clienteB, produtoB) = await CriarBaseAsync(_empresaB, "B");

        await using (var dbA = fixture.CreateDbContext(_empresaA))
        {
            var venda = new Venda(clienteA, FormaPagamento.Fiado, new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc));
            venda.AdicionarItem(produtoA, 1, 100m, 60m);
            dbA.Vendas.Add(venda);
            (await dbA.Clientes.FindAsync(clienteA))!.AdicionarSaldoDevedor(100m);
            await dbA.SaveChangesAsync();
        }

        await using (var dbB = fixture.CreateDbContext(_empresaB))
        {
            var venda = new Venda(clienteB, FormaPagamento.Fiado, new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc));
            venda.AdicionarItem(produtoB, 1, 70m, 40m);
            dbB.Vendas.Add(venda);
            (await dbB.Clientes.FindAsync(clienteB))!.AdicionarSaldoDevedor(70m);
            await dbB.SaveChangesAsync();
        }

        await using var dbConsulta = fixture.CreateDbContext(_empresaA);
        var resultado = await new ObterRelatorioFiadoQueryHandler(
                dbConsulta, new FakeTenantContext(_empresaA), TimeProvider.System)
            .Handle(new ObterRelatorioFiadoQuery(Novembro2026), CancellationToken.None);

        // Controle positivo (o devedor da A) + isolamento (o da B não aparece).
        var devedor = Assert.Single(resultado.Clientes);
        Assert.Equal(clienteA, devedor.ClienteId);
        Assert.Equal(100m, resultado.TotalAReceber);
    }

    [Fact]
    public async Task RelatorioFiado_de_A_nao_soma_recebimento_de_B()
    {
        var (clienteA, _) = await CriarBaseAsync(_empresaA, "A");
        var (clienteB, _) = await CriarBaseAsync(_empresaB, "B");

        await using (var dbA = fixture.CreateDbContext(_empresaA))
        {
            dbA.Pagamentos.Add(new Pagamento(clienteA, 30m, FormaPagamento.Dinheiro,
                data: new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc)));
            await dbA.SaveChangesAsync();
        }

        await using (var dbB = fixture.CreateDbContext(_empresaB))
        {
            dbB.Pagamentos.Add(new Pagamento(clienteB, 90m, FormaPagamento.Dinheiro,
                data: new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc)));
            await dbB.SaveChangesAsync();
        }

        await using var dbConsulta = fixture.CreateDbContext(_empresaA);
        var resultado = await new ObterRelatorioFiadoQueryHandler(
                dbConsulta, new FakeTenantContext(_empresaA), TimeProvider.System)
            .Handle(new ObterRelatorioFiadoQuery(Novembro2026), CancellationToken.None);

        // Controle positivo (30 da A) + isolamento (os 90 da B não entram).
        Assert.Equal(30m, resultado.TotalRecebido);
    }

    [Fact]
    public async Task RelatorioVasilhames_de_A_nao_mostra_emprestimo_de_B()
    {
        var (clienteA, produtoA) = await CriarBaseAsync(_empresaA, "A");
        var (clienteB, produtoB) = await CriarBaseAsync(_empresaB, "B");

        await using (var dbA = fixture.CreateDbContext(_empresaA))
        {
            dbA.EmprestimosVasilhames.Add(new EmprestimoVasilhame(clienteA, produtoA, 3,
                new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc)));
            await dbA.SaveChangesAsync();
        }

        await using (var dbB = fixture.CreateDbContext(_empresaB))
        {
            dbB.EmprestimosVasilhames.Add(new EmprestimoVasilhame(clienteB, produtoB, 9,
                new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc)));
            await dbB.SaveChangesAsync();
        }

        await using var dbConsulta = fixture.CreateDbContext(_empresaA);
        var resultado = await new ObterRelatorioVasilhamesQueryHandler(dbConsulta, new FakeTenantContext(_empresaA))
            .Handle(new ObterRelatorioVasilhamesQuery(Novembro2026), CancellationToken.None);

        // Controle positivo (3 da A) + isolamento (os 9 da B não entram).
        Assert.Equal(3, resultado.TotalEmprestadoNoMes);
        Assert.Equal(clienteA, Assert.Single(resultado.EmAberto).ClienteId);
    }

    [Fact]
    public async Task RelatorioVasilhames_de_A_nao_soma_devolucao_de_B()
    {
        var (_, produtoA) = await CriarBaseAsync(_empresaA, "A");
        var (_, produtoB) = await CriarBaseAsync(_empresaB, "B");

        await using (var dbA = fixture.CreateDbContext(_empresaA))
        {
            dbA.HistoricosEstoque.Add(new HistoricoEstoque(produtoA, TipoHistoricoEstoque.DevolucaoVasilhame,
                "Devolução A", "Devolução", "sistema", quantidade: 2,
                data: new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc)));
            await dbA.SaveChangesAsync();
        }

        await using (var dbB = fixture.CreateDbContext(_empresaB))
        {
            dbB.HistoricosEstoque.Add(new HistoricoEstoque(produtoB, TipoHistoricoEstoque.DevolucaoVasilhame,
                "Devolução B", "Devolução", "sistema", quantidade: 8,
                data: new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc)));
            await dbB.SaveChangesAsync();
        }

        await using var dbConsulta = fixture.CreateDbContext(_empresaA);
        var resultado = await new ObterRelatorioVasilhamesQueryHandler(dbConsulta, new FakeTenantContext(_empresaA))
            .Handle(new ObterRelatorioVasilhamesQuery(Novembro2026), CancellationToken.None);

        // Controle positivo (2 da A) + isolamento (as 8 da B não entram).
        Assert.Equal(2, resultado.TotalDevolvidoNoMes);
    }
}