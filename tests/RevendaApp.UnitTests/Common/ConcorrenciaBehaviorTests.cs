using Mediator;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Common;
using RevendaApp.Web.Common.Behaviors;
using RevendaApp.Web.Features.Clientes.Pagar;

namespace RevendaApp.UnitTests.Common;

/// <summary>
/// O <see cref="ConcorrenciaBehavior{TMessage,TResponse}"/> traduz
/// <see cref="DbUpdateConcurrencyException"/> (token <c>xmin</c> de Produto/Cliente) numa mensagem
/// amigável e limpa o change tracker — sem a limpeza, a próxima operação do mesmo circuito
/// commitava as entidades pendentes da tentativa que o operador acredita ter falhado.
/// </summary>
public class ConcorrenciaBehaviorTests
{
    /// <summary>Mensagem de conflito de teste; o conteúdo vem do contrato compartilhado.</summary>
    private static readonly string Mensagem = Concorrencia.Mensagem;

    [Fact]
    public async Task Handle_DeveTraduzirConflitoEmMensagemAmigavel_PreservandoCausa()
    {
        using var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var behavior = new ConcorrenciaBehavior<RegistrarPagamentoCommandFake, int>(db);
        var original = CriarConflito();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await behavior.Handle(new RegistrarPagamentoCommandFake(), (_, _) => throw original, CancellationToken.None));

        Assert.Equal(Mensagem, ex.Message);
        Assert.Same(original, ex.InnerException);
    }

    [Fact]
    public async Task Handle_DeveLimparChangeTracker_QuandoHaConflito()
    {
        using var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);

        // Entidade pendente (Added, nunca persistida): é o "fantasma" que a limpeza precisa descartar.
        db.Clientes.Add(new Cliente("Maria Souza", "11988887777", "Rua A, 1"));
        Assert.NotEmpty(db.ChangeTracker.Entries());

        var behavior = new ConcorrenciaBehavior<RegistrarPagamentoCommandFake, int>(db);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await behavior.Handle(new RegistrarPagamentoCommandFake(), (_, _) => throw CriarConflito(), CancellationToken.None));

        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Handle_NaoDeveLimparChangeTracker_QuandoNaoHaConflito()
    {
        using var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        db.Clientes.Add(new Cliente("Maria Souza", "11988887777", "Rua A, 1"));
        var behavior = new ConcorrenciaBehavior<RegistrarPagamentoCommandFake, int>(db);

        var resultado = await behavior.Handle(new RegistrarPagamentoCommandFake(), (_, _) => new ValueTask<int>(7), CancellationToken.None);

        Assert.Equal(7, resultado);
        Assert.NotEmpty(db.ChangeTracker.Entries());
    }

    /// <summary>
    /// Pina a <b>ordem</b> do pipeline resolvida pelo DI, que os testes acima não cobrem: eles
    /// constroem o behavior à mão. O Mediator monta o encadeamento a partir de
    /// <c>IEnumerable&lt;IPipelineBehavior&lt;,&gt;&gt;</c> <b>invertendo</b> a lista
    /// (<c>pipelineBehaviours.Reverse()</c> no código gerado), então o <b>primeiro</b> registrado é o
    /// <b>mais externo</b>. Se alguém reordenar ou remover o registro do <c>ConcorrenciaBehavior</c>,
    /// o bug de gravação fantasma do change tracker volta sem nenhum outro teste ficar vermelho.
    /// </summary>
    /// <remarks>
    /// Usa o MESMO <see cref="PipelineDeMediatorExtensions.AddPipelineBehaviors"/> que o
    /// <c>Program.cs</c> chama: um teste que copiasse as duas linhas de registro passaria mesmo se a
    /// aplicação fosse reordenada, e não pinaria nada.
    /// </remarks>
    [Fact]
    public void Registro_NoDi_DeveColocarConcorrenciaBehaviorPorForaDaValidacao()
    {
        var servicos = new ServiceCollection();
        servicos.AddPipelineBehaviors();

        // O ConcorrenciaBehavior recebe o AppDbContext scoped do circuito.
        using var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        servicos.AddScoped(_ => db);

        using var provider = servicos.BuildServiceProvider();
        using var escopo = provider.CreateScope();

        var pipeline = escopo.ServiceProvider
            .GetServices<IPipelineBehavior<RegistrarPagamentoCommand, RegistrarPagamentoResult>>()
            .ToArray();

        Assert.Collection(
            pipeline,
            externo => Assert.IsType<ConcorrenciaBehavior<RegistrarPagamentoCommand, RegistrarPagamentoResult>>(externo),
            interno => Assert.IsType<ValidationBehavior<RegistrarPagamentoCommand, RegistrarPagamentoResult>>(interno));
    }

    /// <summary>
    /// Constrói um <see cref="DbUpdateConcurrencyException"/> real. O behavior só distingue o tipo,
    /// então a mensagem basta (as entradas são opcionais no construtor).
    /// </summary>
    private static DbUpdateConcurrencyException CriarConflito()
        => new("conflito de concorrência simulado");

    /// <summary>
    /// Mensagem mínima que implementa <see cref="Mediator.IMessage"/> para exercitar o behavior sem
    /// depender de um comando real de domínio.
    /// </summary>
    public sealed record RegistrarPagamentoCommandFake : Mediator.ICommand<int>;
}