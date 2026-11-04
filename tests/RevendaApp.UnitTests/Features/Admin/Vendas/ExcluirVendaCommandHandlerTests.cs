using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Common;
using RevendaApp.Web.Common.Authorization;
using RevendaApp.Web.Features.Admin.Vendas;
using RevendaApp.Web.Features.Vendas.NovaVenda;

namespace RevendaApp.UnitTests.Features.Admin.Vendas;

public class ExcluirVendaCommandHandlerTests
{
    private static async Task<(RevendaApp.Infrastructure.AppDbContext Db, Cliente Cliente, Produto Produto, Venda Venda)>
        CriarVendaAsync(FormaPagamento forma = FormaPagamento.Dinheiro, int quantidade = 2, decimal valorEntrega = 0m)
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria Souza", "11988887777", "Rua A, 1");
        db.Clientes.Add(cliente);
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m);
        produto.RegistrarEntrada(20);
        db.Produtos.Add(produto);

        var venda = new Venda(cliente.Id, forma);
        venda.AdicionarItem(produto.Id, quantidade, precoUnitario: 100m, precoCustoUnitario: 60m);
        venda.DefinirValorEntrega(valorEntrega);

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
    public async Task Handle_DeveGravarOInstanteDoTimeProvider_EmExcluidaEm()
    {
        // O handler precisa carimbar o instante do seu TimeProvider (e não DateTime.UtcNow), senão a
        // auditoria não é determinística nem coerente com o relógio usado no resto da operação.
        var (db, _, _, venda) = await CriarVendaAsync(forma: FormaPagamento.Dinheiro, quantidade: 1);
        var instante = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new ExcluirVendaCommandHandler(
            db, TenantTeste.Contexto(), new FakeTimeProvider(instante),
            usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));

        await handler.Handle(new ExcluirVendaCommand(venda.Id), CancellationToken.None);

        var vendaAtual = await db.Vendas.FindAsync(venda.Id);
        Assert.Equal(instante, vendaAtual!.ExcluidaEm);
    }

    [Fact]
    public async Task Handle_DeveExcluirLogicamente_EstornandoEstoqueESaldoFiado()
    {
        var (db, cliente, produto, venda) = await CriarVendaAsync(forma: FormaPagamento.Fiado, quantidade: 2); // 200 fiado
        var handler = new ExcluirVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));

        var resultado = await handler.Handle(new ExcluirVendaCommand(venda.Id), CancellationToken.None);

        Assert.True(resultado.Sucesso);

        // Estoque devolvido: 20 - 2 + 2 = 20.
        var produtoAtual = await db.Produtos.FindAsync(produto.Id);
        Assert.Equal(20, produtoAtual!.QuantidadeEstoque);

        // Saldo fiado abatido: 200 - 200 = 0.
        var clienteAtual = await db.Clientes.FindAsync(cliente.Id);
        Assert.Equal(0m, clienteAtual!.SaldoDevedor);

        // Venda permanece no banco, mas marcada como excluída.
        var vendaAtual = await db.Vendas.FindAsync(venda.Id);
        Assert.NotNull(vendaAtual!.ExcluidaEm);
    }

    [Fact]
    public async Task Handle_ExcluirTroca_ComVasilhamesDisponiveisMenoresQueQtd_DeveClamparSemLancar()
    {
        // Hardening I1: uma venda de troca de 2 (válida), mas o depósito teve o vazio consumido por
        // trocas manuais intersticiais após a venda (Vasilhames caiu a 1). Ao excluir (ReverterAsync),
        // o disponível (1) < qtd revertido (2) — não pode lançar; reverte no máximo o existente.
        var (db, _, produto, venda) = await CriarVendaVasilhameAsync(quantidade: 2, vasilhames: 10);
        Assert.Equal(12, produto.QuantidadeVasilhames); // 10 + 2 da troca

        produto.RegistrarSaidaVasilhame(11); // intersticial
        await db.SaveChangesAsync();
        Assert.Equal(1, produto.QuantidadeVasilhames);

        var handler = new ExcluirVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));
        var resultado = await handler.Handle(new ExcluirVendaCommand(venda.Id), CancellationToken.None);

        Assert.True(resultado.Sucesso);

        var produtoAtual = await db.Produtos.FindAsync(produto.Id);

        // Sem exceção. Vasilhames: 1 → clamp reverte −1 (disponível) → 0. Estoque estornado +2.
        Assert.Equal(0, produtoAtual!.QuantidadeVasilhames);
    }

    private static async Task<(RevendaApp.Infrastructure.AppDbContext Db, Cliente Cliente, Produto Produto, Venda Venda)>
        CriarVendaVasilhameAsync(int quantidade = 2, int estoque = 20, int vasilhames = 10, bool troca = true)
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
                [new ItemVendaInput(produto.Id, quantidade, EhEmprestimo: !troca)]),
            CancellationToken.None);

        var venda = await db.Vendas.FindAsync(resultado.VendaId);
        return (db, cliente, produto, venda!);
    }

    [Fact]
    public async Task Handle_ExcluirEmprestimo_ComLinhasDaVendaAusentes_DeveNoOpSeguro_E_Excluir()
    {
        // Contrato da solução de raiz (VendaId): quando as linhas de empréstimo DESTA venda não são
        // atribuíveis — removidas por divergência manual, ou já quitadas por devolução manual prévia —
        // a reversão NÃO lança (Important 2 resolvido: não suja o DbContext com mutação parcial após
        // exceção). Faz no-op no vasilhame (sem decremento duplo) e a exclusão da venda prossegue.
        var (db, _, produto, venda) = await CriarVendaVasilhameAsync(troca: false, quantidade: 3);
        Assert.Equal(3, produto.QuantidadeVasilhamesEmprestados);
        Assert.Equal(1, db.EmprestimosVasilhames.Count()); // venda de 1 produto consolida em 1 linha (Saldo 3)

        // Simula divergência: as linhas somem, o contador segue 3.
        db.EmprestimosVasilhames.RemoveRange(db.EmprestimosVasilhames);
        await db.SaveChangesAsync();
        Assert.Equal(3, (await db.Produtos.FindAsync(produto.Id))!.QuantidadeVasilhamesEmprestados);

        var handler = new ExcluirVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));
        var resultado = await handler.Handle(new ExcluirVendaCommand(venda.Id), CancellationToken.None);

        // Sem exceção; venda excluída; contador INTACTO (3) — a reversão não decrementou linhas
        // inexistentes (não "consertou" a divergência, mas não corrompeu mais).
        Assert.True(resultado.Sucesso);
        Assert.NotNull((await db.Vendas.FindAsync(venda.Id))!.ExcluidaEm);
        Assert.Equal(3, (await db.Produtos.FindAsync(produto.Id))!.QuantidadeVasilhamesEmprestados);
    }

    [Fact]
    public async Task Handle_ExcluirEmprestimo_AposDevolucaoManual_DeveNoOpSeguro_SemDecrementarDuploNemLancar()
    {
        // Importan 1: entre a venda de empréstimo e a reversão, o admin fez uma devolução manual —
        // o empréstimo da venda foi quitado (DevolvidoEm set) e o contador já reduziu. Ao excluir a
        // venda depois, a reversão NÃO deve dupla-decrementar o contador (fica negativo) nem lançar
        // por "linhas insuficientes" — a linha da venda foi quitada, então a venda não tem mais
        // efeito de empréstimo pendente.
        var (db, _, produto, venda) = await CriarVendaVasilhameAsync(troca: false, quantidade: 2);
        Assert.Equal(2, produto.QuantidadeVasilhamesEmprestados);
        Assert.Equal(1, db.EmprestimosVasilhames.Count());

        // Devolução manual do empréstimo da venda: quita a linha e abaixa o contador → 0.
        var linhaVenda = db.EmprestimosVasilhames.Single();
        linhaVenda.RegistrarDevolucao(2);
        produto.RegistrarDevolucaoVasilhame(2);
        await db.SaveChangesAsync();
        Assert.Equal(0, (await db.Produtos.FindAsync(produto.Id))!.QuantidadeVasilhamesEmprestados);

        var handler = new ExcluirVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));
        var resultado = await handler.Handle(new ExcluirVendaCommand(venda.Id), CancellationToken.None);

        // Sem exceção; contador INTACTO (0 — a devolução manual já quitou); Estoque reabastecido
        // pela reversão (estorno normal da venda).
        Assert.True(resultado.Sucesso);
        var produtoAtual = await db.Produtos.FindAsync(produto.Id);
        Assert.Equal(0, produtoAtual!.QuantidadeVasilhamesEmprestados);
        Assert.Equal(20, produtoAtual.QuantidadeEstoque);
    }

    [Fact]
    public async Task Handle_ExcluirEmprestimo_AposDevolucaoManualParcial_DeveReduzirApenasOSaldoRestante()
    {
        // Variante parcial do Importan 1: a devolução manual quitou PARTE (1 de 3); a linha ficou
        // com DevolvidoEm null e Saldo 2. A reversão da venda deve remover a linha e decrementar
        // SÓ o saldo restante (2), não a quantidade original (3) — senão o contador dupla-decrementa.
        var (db, _, produto, venda) = await CriarVendaVasilhameAsync(troca: false, quantidade: 3);
        var linhaVenda = db.EmprestimosVasilhames.Single();
        linhaVenda.RegistrarDevolucao(1);
        produto.RegistrarDevolucaoVasilhame(1);
        await db.SaveChangesAsync();
        Assert.Equal(2, produto.QuantidadeVasilhamesEmprestados);

        var handler = new ExcluirVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));
        var resultado = await handler.Handle(new ExcluirVendaCommand(venda.Id), CancellationToken.None);

        Assert.True(resultado.Sucesso);
        var produtoAtual = await db.Produtos.FindAsync(produto.Id);
        Assert.Equal(0, produtoAtual!.QuantidadeVasilhamesEmprestados); // 2 (saldo) − 2 (reversão)
        Assert.Empty(db.EmprestimosVasilhames); // linha removida
    }

    [Fact]
    public async Task Handle_DeveRemoverPagamentoDaVendaNaoFiado()
    {
        var (db, _, _, venda) = await CriarVendaAsync(forma: FormaPagamento.Dinheiro, quantidade: 2); // 200, pagamento 200
        Assert.Single(db.Pagamentos);

        var handler = new ExcluirVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));
        await handler.Handle(new ExcluirVendaCommand(venda.Id), CancellationToken.None);

        Assert.Empty(db.Pagamentos);
    }

    [Fact]
    public async Task Handle_DeveBloquearExclusao_QuandoSaldoFiadoNaoCobreValor()
    {
        var (db, cliente, _, venda) = await CriarVendaAsync(forma: FormaPagamento.Fiado, quantidade: 2); // 200 fiado
        // Simula saldo já reduzido (cliente pagou parte): saldo 50 < 200.
        cliente.RegistrarPagamento(150m);
        await db.SaveChangesAsync();

        var handler = new ExcluirVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(new ExcluirVendaCommand(venda.Id), CancellationToken.None).AsTask());

        // Nada mudou: venda segue ativa.
        Assert.Null((await db.Vendas.FindAsync(venda.Id))!.ExcluidaEm);
    }

    [Fact]
    public async Task Handle_DeveBloquearExclusao_QuandoVendaJaExcluida()
    {
        var (db, _, _, venda) = await CriarVendaAsync();
        venda.Excluir();
        await db.SaveChangesAsync();

        var handler = new ExcluirVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(new ExcluirVendaCommand(venda.Id), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_DeveLancar_QuandoVendaNaoExiste()
    {
        using var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var handler = new ExcluirVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(new ExcluirVendaCommand(Guid.NewGuid()), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_DeveLancar_QuandoUsuarioNaoAdmin()
    {
        var (db, _, _, venda) = await CriarVendaAsync();
        var handler = new ExcluirVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: false));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(new ExcluirVendaCommand(venda.Id), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_ExcluirVendaDeEmprestimo_DeveReverterEmprestadosEEstoqueERemoverLinhas()
    {
        // Venda de empréstimo de 2 via RegistrarVendaCommandHandler (caminho real): Estoque 18,
        // Vasilhames 10, Emprestados 2, 1 linha de EmprestimoVasilhame aberta.
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria Souza", "11988887777", "Rua A, 1");
        db.Clientes.Add(cliente);
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m, controlaVasilhame: true);
        produto.RegistrarEntrada(20);
        produto.RegistrarEntradaVasilhame(10);
        db.Produtos.Add(produto);
        await db.SaveChangesAsync();

        var registrarHandler = new RegistrarVendaCommandHandler(db, TenantTeste.Contexto());
        var resultado = await registrarHandler.Handle(
            new RegistrarVendaCommand(cliente.Id, null, null, null, FormaPagamento.Dinheiro, 0m,
                [new ItemVendaInput(produto.Id, 2, EhEmprestimo: true)]),
            CancellationToken.None);
        Assert.Equal(18, (await db.Produtos.FindAsync(produto.Id))!.QuantidadeEstoque);
        Assert.Equal(2, (await db.Produtos.FindAsync(produto.Id))!.QuantidadeVasilhamesEmprestados);
        Assert.Single(db.EmprestimosVasilhames);

        var handler = new ExcluirVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));
        await handler.Handle(new ExcluirVendaCommand(resultado.VendaId), CancellationToken.None);

        var produtoAtual = await db.Produtos.FindAsync(produto.Id);

        // Reversão: Estoque +2 (20), Emprestados −2 (0), linha aberta da venda removida.
        Assert.Equal(20, produtoAtual!.QuantidadeEstoque);
        Assert.Equal(10, produtoAtual.QuantidadeVasilhames);
        Assert.Equal(0, produtoAtual.QuantidadeVasilhamesEmprestados);
        Assert.Empty(db.EmprestimosVasilhames);
    }

    [Fact]
    public async Task Handle_DeveEstornarEstoque_QuandoNaoFiado()
    {
        var (db, _, produto, venda) = await CriarVendaAsync(forma: FormaPagamento.Dinheiro, quantidade: 3); // 300
        var handler = new ExcluirVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));

        await handler.Handle(new ExcluirVendaCommand(venda.Id), CancellationToken.None);

        // Estoque: 20 - 3 + 3 = 20.
        var produtoAtual = await db.Produtos.FindAsync(produto.Id);
        Assert.Equal(20, produtoAtual!.QuantidadeEstoque);
    }

    [Fact]
    public async Task Handle_ExcluirVendaDeEmprestimo_DeveConservarEmprestimoAnterior_QuandoClienteJaTinhaEmprestimoAberto()
    {
        // Cliente já tinha 3 vasilhames em aberto (linha mais antiga). Novo empréstimo de 2 pela
        // venda registra outra linha (mais recente). Ao excluir a venda, apenas a linha da venda é
        // removida (FIFO), preservando os 3 antigos no agregado de Emprestados.
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria Souza", "11988887777", "Rua A, 1");
        db.Clientes.Add(cliente);
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m, controlaVasilhame: true);
        produto.RegistrarEntrada(20);
        produto.RegistrarEntradaVasilhame(10);
        produto.RegistrarEmprestimoVasilhame(3); // contador operacional dos 3 antigos
        db.Produtos.Add(produto);
        db.EmprestimosVasilhames.Add(new EmprestimoVasilhame(
            cliente.Id, produto.Id, 3, data: DateTime.UtcNow.AddDays(-5), observacao: "anterior"));
        await db.SaveChangesAsync();

        var registrarHandler = new RegistrarVendaCommandHandler(db, TenantTeste.Contexto());
        var resultado = await registrarHandler.Handle(
            new RegistrarVendaCommand(cliente.Id, null, null, null, FormaPagamento.Dinheiro, 0m,
                [new ItemVendaInput(produto.Id, 2, EhEmprestimo: true)]),
            CancellationToken.None);

        // 2 linhas abertas: a antiga (3) + a da venda (2); Emprestados 5.
        Assert.Equal(5, (await db.Produtos.FindAsync(produto.Id))!.QuantidadeVasilhamesEmprestados);
        Assert.Equal(2, db.EmprestimosVasilhames.Count(e => e.DevolvidoEm == null));

        var handler = new ExcluirVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));
        await handler.Handle(new ExcluirVendaCommand(resultado.VendaId), CancellationToken.None);

        // Reversão: Emprestados −2 → 3; a linha da venda removida; a antiga (DevolvidoEm null) preservada.
        var produtoAtual = await db.Produtos.FindAsync(produto.Id);
        Assert.Equal(20, produtoAtual!.QuantidadeEstoque);
        Assert.Equal(3, produtoAtual.QuantidadeVasilhamesEmprestados);
        var linhaAntiga = Assert.Single(db.EmprestimosVasilhames.Where(e => e.ClienteId == cliente.Id));
        Assert.Equal(3, linhaAntiga.Quantidade);
        Assert.Null(linhaAntiga.DevolvidoEm);
    }

    /// <summary>Cria cliente + produto com AvisarRecompra e uma venda vigente para o par.</summary>
    private static async Task<(RevendaApp.Infrastructure.AppDbContext Db, Cliente Cliente, Produto Produto, Venda Venda)>
        CriarVendaComRecompraAsync(DateTime dataHora, int quantidade = 1, bool avisarRecompra = true, int diasPadrao = 45)
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria Souza", "11988887777", "Rua A, 1");
        db.Clientes.Add(cliente);
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m, 5,
            avisarRecompra: avisarRecompra, diasPadraoRecompra: diasPadrao);
        produto.RegistrarEntrada(20);
        db.Produtos.Add(produto);

        var venda = new Venda(cliente.Id, FormaPagamento.Dinheiro, dataHora);
        venda.AdicionarItem(produto.Id, quantidade, precoUnitario: 100m, precoCustoUnitario: 60m);
        db.Vendas.Add(venda);
        produto.RegistrarSaida(quantidade);

        await db.SaveChangesAsync();

        // Cria a recorrência como o handler real faria após a venda.
        await RecorrenciaCompraAprendizado.RecalcularAsync(db, cliente.Id, produto.Id, CancellationToken.None);
        await db.SaveChangesAsync();

        return (db, cliente, produto, venda);
    }

    [Fact]
    public async Task Handle_ExcluirComProdutoAvisarRecompra_DeveRecalcularRecorrencia()
    {
        var (db, cliente, produto, venda) = await CriarVendaComRecompraAsync(DateTime.UtcNow.AddDays(-45));

        // Pré-condição: recorrência existe com o par.
        Assert.Single(db.RecorrenciasCompra);

        var handler = new ExcluirVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));
        await handler.Handle(new ExcluirVendaCommand(venda.Id), CancellationToken.None);

        // Após excluir a única venda, a recorrência deve ser removida.
        Assert.Empty(db.RecorrenciasCompra);
    }

    [Fact]
    public async Task Handle_ExcluirUmaDeDuasVendas_DeveRecalcularIntervaloParaAVendaRestante()
    {
        var (db, cliente, produto, vendaNova) = await CriarVendaComRecompraAsync(DateTime.UtcNow.AddDays(-45), quantidade: 1);
        Assert.Single(db.RecorrenciasCompra);

        // Adiciona uma 2ª venda mais antiga (cria um gap, gerando 2 compras / intervalo).
        var vendaAntiga = new Venda(cliente.Id, FormaPagamento.Dinheiro, DateTime.UtcNow.AddDays(-90));
        vendaAntiga.AdicionarItem(produto.Id, 1, 100m, 60m);
        db.Vendas.Add(vendaAntiga);
        produto.RegistrarSaida(1);
        await db.SaveChangesAsync();
        await RecorrenciaCompraAprendizado.RecalcularAsync(db, cliente.Id, produto.Id, CancellationToken.None);
        await db.SaveChangesAsync();

        var recAntes = db.RecorrenciasCompra.Single();
        Assert.Equal(2, recAntes.QuantidadeCompras);

        // Exclui a venda MAIS NOVA (vendaNova): restam só a antiga → recorrência vira 1 compra / default.
        var handler = new ExcluirVendaCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));
        await handler.Handle(new ExcluirVendaCommand(vendaNova.Id), CancellationToken.None);

        var recDepois = db.RecorrenciasCompra.Single();
        Assert.Equal(1, recDepois.QuantidadeCompras);
        Assert.Equal(45m, recDepois.IntervaloMedioDias);
        Assert.Equal(vendaAntiga.DataHora, recDepois.DataUltimaCompra);
    }
}