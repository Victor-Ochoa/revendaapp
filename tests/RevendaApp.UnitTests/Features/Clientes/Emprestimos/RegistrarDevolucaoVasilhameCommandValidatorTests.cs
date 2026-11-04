using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.Infrastructure;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Features.Clientes.Emprestimos;

namespace RevendaApp.UnitTests.Features.Clientes.Emprestimos;

/// <summary>
/// A devolução manual de vasilhames só faz sentido para um cliente e um produto que existem na
/// empresa: os dois são procurados pelo filtro global do tenant e, sem a validação, a falha
/// apareceria como "não encontrado" no handler (ou como exceção) em vez de mensagem de campo.
/// </summary>
public class RegistrarDevolucaoVasilhameCommandValidatorTests
{
    private static async Task<AppDbContext> CriarAsync()
    {
        var db = InMemoryDbContextFactory.Create();
        var cliente = new Cliente("Maria Souza", "11988887777", "Rua A, 1");
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m, controlaVasilhame: true);
        db.Clientes.Add(cliente);
        db.Produtos.Add(produto);
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task Validate_DeveSerValido_QuandoClienteEProdutoExistem_EQuantidadePositiva()
    {
        using var db = await CriarAsync();
        var validator = new RegistrarDevolucaoVasilhameCommandValidator(db);

        var result = await validator.ValidateAsync(
            new RegistrarDevolucaoVasilhameCommand(db.Clientes.First().Id, db.Produtos.First().Id, 2));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Validate_DeveSerInvalido_QuandoClienteNaoExiste()
    {
        using var db = await CriarAsync();
        var validator = new RegistrarDevolucaoVasilhameCommandValidator(db);

        var result = await validator.ValidateAsync(
            new RegistrarDevolucaoVasilhameCommand(Guid.NewGuid(), db.Produtos.First().Id, 2));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RegistrarDevolucaoVasilhameCommand.ClienteId));
    }

    [Fact]
    public async Task Validate_DeveSerInvalido_QuandoProdutoNaoExiste()
    {
        using var db = await CriarAsync();
        var validator = new RegistrarDevolucaoVasilhameCommandValidator(db);

        var result = await validator.ValidateAsync(
            new RegistrarDevolucaoVasilhameCommand(db.Clientes.First().Id, Guid.NewGuid(), 2));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RegistrarDevolucaoVasilhameCommand.ProdutoId));
    }

    [Fact]
    public async Task Validate_DeveSerInvalido_QuandoQuantidadeNaoEPositiva()
    {
        using var db = await CriarAsync();
        var validator = new RegistrarDevolucaoVasilhameCommandValidator(db);

        var result = await validator.ValidateAsync(
            new RegistrarDevolucaoVasilhameCommand(db.Clientes.First().Id, db.Produtos.First().Id, 0));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RegistrarDevolucaoVasilhameCommand.Quantidade));
    }

    [Fact]
    public async Task Validate_DeveSerInvalido_QuandoObservacaoExcedeLimite()
    {
        using var db = await CriarAsync();
        var validator = new RegistrarDevolucaoVasilhameCommandValidator(db);

        var result = await validator.ValidateAsync(new RegistrarDevolucaoVasilhameCommand(
            db.Clientes.First().Id, db.Produtos.First().Id, 1, new string('x', 301)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RegistrarDevolucaoVasilhameCommand.Observacao));
    }
}