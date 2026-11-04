using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Common;
using RevendaApp.Web.Common.Authorization;
using RevendaApp.Web.Features.Admin.Vendas;
using RevendaApp.Web.Features.Vendas.NovaVenda;

namespace RevendaApp.UnitTests.Features.Admin.Vendas;

public class EditarVendaCommandHandlerTests
{
    private static async Task<(RevendaApp.Infrastructure.AppDbContext Db, Cliente Cliente, Produto Produto, Venda Venda)>
        CriarVendaAsync(FormaPagamento forma = FormaPagamento.Dinheiro, decimal desconto = 0m, int quantidade = 2, decimal valorEntrega = 0m)
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria Souza", "11988887777", "Rua A, 1");
        db.Clientes.Add(cliente);
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m);
        produto.RegistrarEntrada(20);
        db.Produtos.Add(produto);

        var venda = new Venda(cliente.Id, forma);
        venda.AdicionarItem(produto.Id, quantidade, precoUnitario: 100m, precoCustoUnitario: 60m);
        if (desconto > 0)
        {
            venda.AplicarDesconto(desconto);
        }

        venda.DefinirValorEntrega(valorEntrega);

        // Persiste a venda e seus efeitos (estoque + saldo) de forma realista:
        db.Vendas.Add(venda);
        produto.RegistrarSaida(quantidade);
        if (forma == FormaPagamento.Fiado)
        {
            cliente.AdicionarSaldoDevedor(venda.ValorTotal);
        }
        else
        {
            db.Pagamentos.Add(new Pagamento(cliente.Id, venda.ValorTotal, forma, "Venda", vendaId: venda.Id));
        }

        await db.SaveChangesAsync();
        return (db, cliente, produto, venda);
    }

    [Fact]
    public async Task Handle_DeveGravarOInstanteDoTimeProvider_NaVendaAntigaExcluida()
    {
        // A edição exclui logicamente a venda original; o instante gravado deve vir do TimeProvider
        // do handler (auditoria determinística), não do relógio do sistema.
        var (db, _, produto, venda) = await CriarVendaAsync(forma: FormaPagamento.Dinheiro, quantidade: 2);
        var instante = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new EditarVendaCommandHandler(
            db, TenantTeste.Contexto(), new FakeTimeProvider(instante),
            usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));

        await handler.Handle(
            new EditarVendaCommand(venda.Id, FormaPagamento.Dinheiro, 0m, [new ItemVendaInput(produto.Id, 2)]),
            CancellationToken.None);

        var vendaAntiga = await db.Vendas.FindAsync(venda.Id);
        Assert.Equal(instante, vendaAntiga!.ExcluidaEm);
    }

    [Fact]
    public async Task Handle_DeveEditarParaNovaQuantidade_AtualizandoEstoqueELucro()
    {
        var (db, cliente, produto, venda) = await CriarVendaAsync(forma: FormaPagamento.Dinheiro, quantidade: 2);
        var handler = new EditarVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));

        var resultado = await handler.Handle(
            new EditarVendaCommand(venda.Id, FormaPagamento.Dinheiro, 0m,
                [new ItemVendaInput(produto.Id, 3)]),
            CancellationToken.None);

        // Estoque: 20 - 2 (venda antiga) + 2 (reversão) - 3 (nova) = 17.
        var produtoAtual = await db.Produtos.FindAsync(produto.Id);
        Assert.Equal(17, produtoAtual!.QuantidadeEstoque);

        // Nova venda criada, antiga marcada excluída.
        var vendas = db.Vendas.ToList();
        Assert.Equal(2, vendas.Count);
        Assert.Single(vendas, v => v.ExcluidaEm == null); // só a nova ativa
        Assert.Single(vendas, v => v.ExcluidaEm != null); // a antiga excluída

        Assert.Equal(300m, resultado.ValorTotal);
    }

    [Fact]
    public async Task Handle_RegraFiado_DeveRecalcularSaldo_QuandoSaldoCobreValor()
    {
        // Fiado: cliente tem saldo devedor (aberto) que cobre a venda.
        var (db, cliente, produto, venda) = await CriarVendaAsync(forma: FormaPagamento.Fiado, quantidade: 2); // 200, cliente +200 saldo
        var handler = new EditarVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));

        // Novo total: 100 (1 item) -> o saldo deve recalcular: 200 - 200(antiga) + 100(nova) = 100.
        var resultado = await handler.Handle(
            new EditarVendaCommand(venda.Id, FormaPagamento.Fiado, 0m,
                [new ItemVendaInput(produto.Id, 1)]),
            CancellationToken.None);

        Assert.Equal(100m, resultado.ValorTotal);
        Assert.Equal(100m, resultado.SaldoDevedor);

        var clienteAtual = await db.Clientes.FindAsync(cliente.Id);
        Assert.Equal(100m, clienteAtual!.SaldoDevedor);
    }

    [Fact]
    public async Task Handle_RegraFiado_DeveBloquear_QuandoSaldoNaoCobreValor()
    {
        // Fiado quitado: cliente tem saldo que não cobre (já pagou).
        var (db, _, produto, venda) = await CriarVendaAsync(forma: FormaPagamento.Fiado, quantidade: 2); // 200 fiado
        var cliente = await db.Clientes.FindAsync(db.Clientes.First().Id);

        // Simula cliente que já quitou a dívida (saldo menor que o valor).
        db.Attach(cliente!);
        cliente!.RegistrarPagamento(150m); // saldo 50 < 200
        await db.SaveChangesAsync();

        var handler = new EditarVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(
                new EditarVendaCommand(venda.Id, FormaPagamento.Fiado, 0m,
                    [new ItemVendaInput(produto.Id, 1)]),
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_DeveBloquearEdicao_QuandoVendaJaExcluida()
    {
        var (db, _, produto, venda) = await CriarVendaAsync();
        venda.Excluir();
        await db.SaveChangesAsync();

        var handler = new EditarVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(
                new EditarVendaCommand(venda.Id, FormaPagamento.Dinheiro, 0m,
                    [new ItemVendaInput(produto.Id, 1)]),
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_DeveLancar_QuandoVendaNaoExiste()
    {
        using var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var handler = new EditarVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(
                new EditarVendaCommand(Guid.NewGuid(), FormaPagamento.Dinheiro, 0m,
                    [new ItemVendaInput(Guid.NewGuid(), 1)]),
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_DeveLancar_QuandoUsuarioNaoAdmin()
    {
        var (db, _, produto, venda) = await CriarVendaAsync();
        var handler = new EditarVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: false));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(
                new EditarVendaCommand(venda.Id, FormaPagamento.Dinheiro, 0m,
                    [new ItemVendaInput(produto.Id, 1)]),
                CancellationToken.None).AsTask());
    }

    /// <summary>
    /// Cria cliente + produto com controle de vasilhame e registra uma venda de vasilhame
    /// (empréstimo ou troca) via <see cref="RegistrarVendaCommandHandler"/> — o mesmo caminho real,
    /// para que Emprestados/linhas de EmprestimoVasilhame fiquem consistentes com o que a reversão
    /// posterior vai estornar.
    /// </summary>
    private static async Task<(RevendaApp.Infrastructure.AppDbContext Db, Cliente Cliente, Produto Produto, Venda Venda)>
        CriarVendaVasilhameAsync(bool ehEmprestimo, int quantidade = 2, int estoque = 20, int vasilhames = 10)
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria Souza", "11988887777", "Rua A, 1");
        db.Clientes.Add(cliente);
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m, controlaVasilhame: true);
        produto.RegistrarEntrada(estoque);
        produto.RegistrarEntradaVasilhame(vasilhames);
        db.Produtos.Add(produto);
        await db.SaveChangesAsync();

        var registrarHandler = new RegistrarVendaCommandHandler(db, TenantTeste.Contexto());
        var resultado = await registrarHandler.Handle(
            new RegistrarVendaCommand(cliente.Id, null, null, null, FormaPagamento.Dinheiro, 0m,
                [new ItemVendaInput(produto.Id, quantidade, EhEmprestimo: ehEmprestimo)]),
            CancellationToken.None);

        var venda = await db.Vendas.FindAsync(resultado.VendaId);
        return (db, cliente, produto, venda!);
    }

    [Fact]
    public async Task Handle_EditarVendaDeEmprestimo_DeveReverterEmprestadosRemoverLinhasEEstoque()
    {
        // Venda de empréstimo (2): Estoque 18, Vasilhames 10, Emprestados 2 + 1 linha aberta.
        var (db, _, produto, venda) = await CriarVendaVasilhameAsync(ehEmprestimo: true, quantidade: 2);
        Assert.Equal(18, produto.QuantidadeEstoque);
        Assert.Equal(10, produto.QuantidadeVasilhames);
        Assert.Equal(2, produto.QuantidadeVasilhamesEmprestados);
        Assert.Single(db.EmprestimosVasilhames);

        // Edita para uma troca de 1 (deixa de ser empréstimo) — isola a observação da reversão.
        var handler = new EditarVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));
        await handler.Handle(
            new EditarVendaCommand(venda.Id, FormaPagamento.Dinheiro, 0m,
                [new ItemVendaInput(produto.Id, 1)]),
            CancellationToken.None);

        var produtoAtual = await db.Produtos.FindAsync(produto.Id);

        // Reversão da venda antiga: Estoque +2 (20) e Emprestados −2 (0), linha aberta removida.
        // Nova venda (troca de 1): Estoque −1 (19) e Vasilhames +1 (11).
        Assert.Equal(19, produtoAtual!.QuantidadeEstoque);
        Assert.Equal(11, produtoAtual.QuantidadeVasilhames);
        Assert.Equal(0, produtoAtual.QuantidadeVasilhamesEmprestados);
        Assert.Empty(db.EmprestimosVasilhames);
    }

    [Fact]
    public async Task Handle_EditarVendaDeTroca_DeveReverterVasilhamesNoDeposito()
    {
        // Venda de troca (2): Estoque 18, Vasilhames 12, sem empréstimos.
        var (db, _, produto, venda) = await CriarVendaVasilhameAsync(ehEmprestimo: false, quantidade: 2);
        Assert.Equal(18, produto.QuantidadeEstoque);
        Assert.Equal(12, produto.QuantidadeVasilhames);
        Assert.Equal(0, produto.QuantidadeVasilhamesEmprestados);

        var handler = new EditarVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));
        await handler.Handle(
            new EditarVendaCommand(venda.Id, FormaPagamento.Dinheiro, 0m,
                [new ItemVendaInput(produto.Id, 1)]),
            CancellationToken.None);

        var produtoAtual = await db.Produtos.FindAsync(produto.Id);

        // Reversão da troca antiga: Estoque +2 (20), Vasilhames −2 (10). Nova troca de 1:
        // Estoque −1 (19), Vasilhames +1 (11).
        Assert.Equal(19, produtoAtual!.QuantidadeEstoque);
        Assert.Equal(11, produtoAtual.QuantidadeVasilhames);
        Assert.Equal(0, produtoAtual.QuantidadeVasilhamesEmprestados);
    }

    [Fact]
    public async Task Handle_DevePreservarDataHoraOriginal_QuandoVendaEditada()
    {
        var (db, _, produto, venda) = await CriarVendaAsync(forma: FormaPagamento.Dinheiro, quantidade: 2);
        var dataHoraOriginal = venda.DataHora;
        var handler = new EditarVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));

        await handler.Handle(
            new EditarVendaCommand(venda.Id, FormaPagamento.Dinheiro, 0m,
                [new ItemVendaInput(produto.Id, 1)]),
            CancellationToken.None);

        // A nova venda ativa deve manter a DataHora da original — a edição não pode resetar para UtcNow.
        var novaVenda = db.Vendas.Single(v => v.ExcluidaEm == null);
        Assert.Equal(dataHoraOriginal, novaVenda.DataHora);
    }

    /// <summary>Cria cliente + produto com AvisarRecompra + venda vigente e recorrência inicial.</summary>
    private static async Task<(RevendaApp.Infrastructure.AppDbContext Db, Cliente Cliente, Produto Produto, Venda Venda)>
        CriarVendaComRecompraAsync(int quantidade = 2)
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria Souza", "11988887777", "Rua A, 1");
        db.Clientes.Add(cliente);
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m, 5,
            avisarRecompra: true, diasPadraoRecompra: 45);
        produto.RegistrarEntrada(20);
        db.Produtos.Add(produto);

        var venda = new Venda(cliente.Id, FormaPagamento.Dinheiro);
        venda.AdicionarItem(produto.Id, quantidade, 100m, 60m);
        db.Vendas.Add(venda);
        produto.RegistrarSaida(quantidade);
        db.Pagamentos.Add(new Pagamento(cliente.Id, venda.ValorTotal, FormaPagamento.Dinheiro, "Venda", vendaId: venda.Id));

        await db.SaveChangesAsync();
        await RecorrenciaCompraAprendizado.RecalcularAsync(db, cliente.Id, produto.Id, CancellationToken.None);
        await db.SaveChangesAsync();

        return (db, cliente, produto, venda);
    }

    [Fact]
    public async Task Handle_ProdutoComAvisarRecompra_DeveReconstruirRecorrencia_AposEdicao()
    {
        var (db, cliente, produto, venda) = await CriarVendaComRecompraAsync(quantidade: 2);

        // Pré-condição: recorrência existe (2 compras? não — 1 venda vigente → qtd 1, default 45).
        var recAntes = db.RecorrenciasCompra.Single();
        Assert.Equal(1, recAntes.QuantidadeCompras);
        Assert.Equal(45m, recAntes.IntervaloMedioDias);

        var handler = new EditarVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));
        await handler.Handle(
            new EditarVendaCommand(venda.Id, FormaPagamento.Dinheiro, 0m,
                [new ItemVendaInput(produto.Id, 1)]),
            CancellationToken.None);

        // Após a edição (antiga excluída + nova criada), a recorrência deve existir apontando para a nova venda.
        var recDepois = Assert.Single(db.RecorrenciasCompra);
        var novaVenda = db.Vendas.Single(v => v.ExcluidaEm == null);
        Assert.Equal(novaVenda.DataHora, recDepois.DataUltimaCompra);
        Assert.Equal(1, recDepois.QuantidadeCompras);
        Assert.Equal(45m, recDepois.IntervaloMedioDias);
    }

    [Fact]
    public async Task Handle_EditarRemovendoProdutoAvisador_DeveRecalcularESemRecorrenciaSeSemVenda()
    {
        // Setup: cliente compra produto A (avisador) e produto B. Edita para REMOVER o A.
        // Como o A não tem mais venda vigente → recorrência de A removida.
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria Souza", "11988887777", "Rua A, 1");
        db.Clientes.Add(cliente);
        var produtoA = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m, 5,
            avisarRecompra: true, diasPadraoRecompra: 45);
        produtoA.RegistrarEntrada(20);
        var produtoB = new Produto("Água 20L", TipoProduto.Agua, 25m, 12m, 18m, 5);
        produtoB.RegistrarEntrada(20);
        db.Produtos.AddRange(produtoA, produtoB);

        var venda = new Venda(cliente.Id, FormaPagamento.Dinheiro);
        venda.AdicionarItem(produtoA.Id, 1, 100m, 60m);
        venda.AdicionarItem(produtoB.Id, 1, 25m, 12m);
        db.Vendas.Add(venda);
        produtoA.RegistrarSaida(1);
        produtoB.RegistrarSaida(1);
        db.Pagamentos.Add(new Pagamento(cliente.Id, venda.ValorTotal, FormaPagamento.Dinheiro, "Venda", vendaId: venda.Id));

        await db.SaveChangesAsync();
        await RecorrenciaCompraAprendizado.RecalcularAsync(db, cliente.Id, produtoA.Id, CancellationToken.None);
        await db.SaveChangesAsync();
        Assert.Single(db.RecorrenciasCompra);

        // Edita removendo o produto A (só B).
        var handler = new EditarVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));
        await handler.Handle(
            new EditarVendaCommand(venda.Id, FormaPagamento.Dinheiro, 0m,
                [new ItemVendaInput(produtoB.Id, 1)]),
            CancellationToken.None);

        Assert.Empty(db.RecorrenciasCompra);
    }
}