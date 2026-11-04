using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.Infrastructure;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Features.Clientes.Emprestimos;

namespace RevendaApp.UnitTests.Features.Clientes.Emprestimos;

public class RegistrarDevolucaoVasilhameCommandHandlerTests
{
    private static async Task<(AppDbContext Db, Cliente Cliente, Produto Produto)> CriarClienteEProdutoAsync(
        int estoqueCheio = 0, bool controlaVasilhame = true)
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria Souza", "11988887777", "Rua A, 1");
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m, controlaVasilhame: controlaVasilhame);
        if (estoqueCheio > 0)
        {
            produto.RegistrarEntrada(estoqueCheio);
        }

        db.Clientes.Add(cliente);
        db.Produtos.Add(produto);
        await db.SaveChangesAsync();
        return (db, cliente, produto);
    }

    private static async Task<EmprestimoVasilhame> RegistrarEmprestimoAsync(
        AppDbContext db,
        Guid clienteId,
        Guid produtoId,
        int quantidade,
        DateTime? data = null)
    {
        var produto = await db.Produtos.FindAsync(produtoId);
        produto!.RegistrarEmprestimoVasilhame(quantidade);
        var emprestimo = new EmprestimoVasilhame(clienteId, produtoId, quantidade, data, null);
        db.EmprestimosVasilhames.Add(emprestimo);
        await db.SaveChangesAsync();
        return emprestimo;
    }

    [Fact]
    public async Task Handle_DeveQuitarEmprestimo_ReduzirEmprestados_E_DevolverVaziosAoDisponivel()
    {
        var (db, cliente, produto) = await CriarClienteEProdutoAsync(estoqueCheio: 10);
        var emprestimo = await RegistrarEmprestimoAsync(db, cliente.Id, produto.Id, 5);
        var handler = new RegistrarDevolucaoVasilhameCommandHandler(
            db,
            TenantTeste.Contexto(),
            TimeProvider.System,
            new FakeAuthenticationStateProvider("maria@teste.com"),
            new FakeUsuarioAutenticado(ehAdmin: true));

        var sucesso = await handler.Handle(
            new RegistrarDevolucaoVasilhameCommand(cliente.Id, produto.Id, 5),
            CancellationToken.None);

        Assert.True(sucesso);

        var produtoAtualizado = await db.Produtos.FindAsync(produto.Id);
        Assert.Equal(0, produtoAtualizado!.QuantidadeVasilhamesEmprestados);
        Assert.Equal(10, produtoAtualizado.QuantidadeEstoque); // Estoque intocado
        Assert.Equal(5, produtoAtualizado.QuantidadeVasilhames); // vazios voltam ao disponível

        var emprestimoAtualizado = await db.EmprestimosVasilhames.FindAsync(emprestimo.Id);
        Assert.NotNull(emprestimoAtualizado!.DevolvidoEm);
        Assert.Equal(0, emprestimoAtualizado.Saldo);

        var historico = Assert.Single(db.HistoricosEstoque);
        Assert.Equal(TipoHistoricoEstoque.DevolucaoVasilhame, historico.Tipo);
        Assert.Equal(5, historico.Quantidade);
        Assert.Equal("maria@teste.com", historico.Usuario);
        Assert.Contains("estoque de vazios disponíveis", historico.Descricao);
    }

    [Fact]
    public async Task Handle_DeveGravarOInstanteDoTimeProvider_EmDevolvidoEm()
    {
        // A quitação do empréstimo grava DevolvidoEm; o instante precisa vir do TimeProvider do
        // handler para a auditoria ser determinística e coerente com o restante da operação.
        var (db, cliente, produto) = await CriarClienteEProdutoAsync(estoqueCheio: 10);
        var emprestimo = await RegistrarEmprestimoAsync(db, cliente.Id, produto.Id, 5);
        var instante = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new RegistrarDevolucaoVasilhameCommandHandler(
            db, TenantTeste.Contexto(), new FakeTimeProvider(instante),
            usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));

        await handler.Handle(
            new RegistrarDevolucaoVasilhameCommand(cliente.Id, produto.Id, 5),
            CancellationToken.None);

        var emprestimoAtualizado = await db.EmprestimosVasilhames.FindAsync(emprestimo.Id);
        Assert.Equal(instante, emprestimoAtualizado!.DevolvidoEm);
    }

    [Fact]
    public async Task Handle_SemEstoqueCheio_DeveQuitarEmprestimo_EDevolverVaziosAoDisponivel()
    {
        // A devolução de vazio volta ao estoque de vazios disponíveis, independente de estoque cheio
        // (a decisão anterior de "reabastecer o Estoque" foi revertida — devolução NÃO toca Estoque).
        var (db, cliente, produto) = await CriarClienteEProdutoAsync(estoqueCheio: 2); // < 5 devolvidos
        var emprestimo = await RegistrarEmprestimoAsync(db, cliente.Id, produto.Id, 5);
        var handler = new RegistrarDevolucaoVasilhameCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));

        var sucesso = await handler.Handle(
            new RegistrarDevolucaoVasilhameCommand(cliente.Id, produto.Id, 5),
            CancellationToken.None);

        Assert.True(sucesso);

        var produtoAtualizado = await db.Produtos.FindAsync(produto.Id);
        Assert.Equal(0, produtoAtualizado!.QuantidadeVasilhamesEmprestados);
        Assert.Equal(2, produtoAtualizado.QuantidadeEstoque); // Estoque intacto
        Assert.Equal(5, produtoAtualizado.QuantidadeVasilhames); // vazios devolvidos ao disponível

        var emprestimoAtualizado = await db.EmprestimosVasilhames.FindAsync(emprestimo.Id);
        Assert.NotNull(emprestimoAtualizado!.DevolvidoEm);

        var historico = Assert.Single(db.HistoricosEstoque);
        Assert.Contains("estoque de vazios disponíveis", historico.Descricao);
    }

    [Fact]
    public async Task Handle_DevolucaoParcial_DeveReduzirEmprestados_ManterEmprestimoAberto_EDevolverVaziosAoDisponivel()
    {
        var (db, cliente, produto) = await CriarClienteEProdutoAsync(estoqueCheio: 10);
        var emprestimo = await RegistrarEmprestimoAsync(db, cliente.Id, produto.Id, 10);
        var handler = new RegistrarDevolucaoVasilhameCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));

        var sucesso = await handler.Handle(
            new RegistrarDevolucaoVasilhameCommand(cliente.Id, produto.Id, 4),
            CancellationToken.None);

        Assert.True(sucesso);

        var produtoAtualizado = await db.Produtos.FindAsync(produto.Id);
        Assert.Equal(6, produtoAtualizado!.QuantidadeVasilhamesEmprestados);
        Assert.Equal(10, produtoAtualizado.QuantidadeEstoque); // Estoque intocado
        Assert.Equal(4, produtoAtualizado.QuantidadeVasilhames); // 4 vazios ao disponível

        var emprestimoAtualizado = await db.EmprestimosVasilhames.FindAsync(emprestimo.Id);
        Assert.Equal(4, emprestimoAtualizado!.QuantidadeDevolvida);
        Assert.Equal(6, emprestimoAtualizado.Saldo);
        Assert.Null(emprestimoAtualizado.DevolvidoEm);
    }

    [Fact]
    public async Task Handle_DevolucaoMaiorQueSaldo_DeveLancarExcecao_SemNadaPersistido()
    {
        var (db, cliente, produto) = await CriarClienteEProdutoAsync(estoqueCheio: 10);
        var emprestimo = await RegistrarEmprestimoAsync(db, cliente.Id, produto.Id, 5);
        var handler = new RegistrarDevolucaoVasilhameCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(
                new RegistrarDevolucaoVasilhameCommand(cliente.Id, produto.Id, 6),
                CancellationToken.None).AsTask());

        Assert.Equal("Quantidade devolvida não pode exceder os vasilhames emprestados", ex.Message);

        var produtoAtualizado = await db.Produtos.FindAsync(produto.Id);
        Assert.Equal(5, produtoAtualizado!.QuantidadeVasilhamesEmprestados);
        Assert.Equal(10, produtoAtualizado.QuantidadeEstoque);
        Assert.Empty(db.HistoricosEstoque);

        var emprestimoAtualizado = await db.EmprestimosVasilhames.FindAsync(emprestimo.Id);
        Assert.Equal(0, emprestimoAtualizado!.QuantidadeDevolvida);
        Assert.Null(emprestimoAtualizado.DevolvidoEm);
    }

    [Fact]
    public async Task Handle_Fifo_DeveQuitarEmprestimosMaisAntigosPrimeiro()
    {
        var (db, cliente, produto) = await CriarClienteEProdutoAsync(estoqueCheio: 10);
        var antigo = await RegistrarEmprestimoAsync(db, cliente.Id, produto.Id, 6, new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc));
        var novo = await RegistrarEmprestimoAsync(db, cliente.Id, produto.Id, 4, new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc));
        var handler = new RegistrarDevolucaoVasilhameCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: true));

        var sucesso = await handler.Handle(
            new RegistrarDevolucaoVasilhameCommand(cliente.Id, produto.Id, 6),
            CancellationToken.None);

        Assert.True(sucesso);

        // Devolve 6: quita integralmente o empréstimo mais antigo; o mais novo permanece intacto.
        var antigoAtualizado = await db.EmprestimosVasilhames.FindAsync(antigo.Id);
        Assert.Equal(6, antigoAtualizado!.QuantidadeDevolvida);
        Assert.NotNull(antigoAtualizado.DevolvidoEm);

        var novoAtualizado = await db.EmprestimosVasilhames.FindAsync(novo.Id);
        Assert.Equal(0, novoAtualizado!.QuantidadeDevolvida);
        Assert.Null(novoAtualizado.DevolvidoEm);

        var produtoAtualizado = await db.Produtos.FindAsync(produto.Id);
        Assert.Equal(4, produtoAtualizado!.QuantidadeVasilhamesEmprestados);
        Assert.Equal(10, produtoAtualizado.QuantidadeEstoque); // Estoque intocado
        Assert.Equal(6, produtoAtualizado.QuantidadeVasilhames); // 6 vazios devolvidos ao disponível
    }

    [Fact]
    public async Task Handle_DeveLancar_QuandoNaoAdmin()
    {
        var (db, cliente, produto) = await CriarClienteEProdutoAsync(estoqueCheio: 10);
        await RegistrarEmprestimoAsync(db, cliente.Id, produto.Id, 5);
        var handler = new RegistrarDevolucaoVasilhameCommandHandler(db, TenantTeste.Contexto(), TimeProvider.System, usuarioAutenticado: new FakeUsuarioAutenticado(ehAdmin: false));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(
                new RegistrarDevolucaoVasilhameCommand(cliente.Id, produto.Id, 2),
                CancellationToken.None).AsTask());

        var produtoAtualizado = await db.Produtos.FindAsync(produto.Id);
        Assert.Equal(5, produtoAtualizado!.QuantidadeVasilhamesEmprestados);
        Assert.Equal(10, produtoAtualizado.QuantidadeEstoque);
        Assert.Empty(db.HistoricosEstoque);
    }
}