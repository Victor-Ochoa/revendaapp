using Mediator;

using RevendaApp.Web.Common.Behaviors;

namespace RevendaApp.UnitTests.Common;

/// <summary>
/// O <see cref="AppDbContext"/> é <b>scoped ao circuito Blazor</b> e compartilhado por todos os
/// componentes dele. O EF proíbe duas operações simultâneas no mesmo contexto
/// (<see cref="InvalidOperationException"/> "A second operation was started on this context
/// instance"). Dois componentes que consultam no <c>OnInitializedAsync</c> colidem: o render do
/// filho acontece durante o <c>await</c> do pai.
/// </summary>
/// <remarks>
/// O <see cref="SerializacaoDbContextBehavior{TMessage,TResponse}"/> é o portão único do pipeline:
/// ele serializa o <b>pipeline inteiro</b> (validação + handler), então qualquer consulta que passe
/// pelo Mediator fica protegida, inclusive as disparadas por abas e por handlers que notificam
/// outros componentes.
/// </remarks>
public class SerializacaoDbContextBehaviorTests
{
    /// <summary>
    /// O caso que originou o bug: duas operações concorrentes no mesmo circuito não podem se
    /// sobrepor. Sem o portão, <c>maximo</c> chega a 4 e o EF lança.
    /// </summary>
    [Fact]
    public async Task Handle_DeveSerializar_QuandoDuasOperacoesConcorremNoMesmoCircuito()
    {
        using var semaforo = new SemaforoDoCircuito();
        var behavior = new SerializacaoDbContextBehavior<MensagemFake, int>(semaforo);

        var simultaneas = 0;
        var maximoSimultaneo = 0;

        async ValueTask<int> Next(MensagemFake _, CancellationToken cancellationToken)
        {
            var atual = Interlocked.Increment(ref simultaneas);
            maximoSimultaneo = Math.Max(maximoSimultaneo, atual);
            await Task.Delay(40, cancellationToken);
            Interlocked.Decrement(ref simultaneas);
            return atual;
        }

        var tarefas = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => behavior.Handle(new MensagemFake(), Next, CancellationToken.None).AsTask()))
            .ToArray();

        await Task.WhenAll(tarefas);

        Assert.Equal(1, maximoSimultaneo);
    }

    /// <summary>
    /// Reentrância: um handler pode disparar outro <c>Send</c> na mesma cadeia
    /// (<c>RegistrarPagamentoCommandHandler</c> → <c>VendasAtualizadasNotifier</c> →
    /// <c>Dashboard.RecarregarAsync</c> → <c>ObterDashboardQuery</c>). Esse fluxo já segura o portão;
    /// esperar por ele seria <b>deadlock</b> — o handler nunca termina e o circuito trava.
    /// </summary>
    [Fact]
    public async Task Handle_DevePermitirReentranciaNaMesmaCadeia_SemDeadlock()
    {
        using var semaforo = new SemaforoDoCircuito();
        var behavior = new SerializacaoDbContextBehavior<MensagemFake, int>(semaforo);

        var resultado = await behavior
            .Handle(
                new MensagemFake(),
                async (_, cancellationToken) => await behavior.Handle(
                    new MensagemFake(),
                    (_, _) => new ValueTask<int>(42),
                    cancellationToken),
                CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(42, resultado);
    }

    /// <summary>
    /// A marca de reentrância é <b>local ao fluxo assíncrono</b> e não pode vazar para o chamador:
    /// se vazasse, todas as consultas seguintes daquele componente pularia o portão e a proteção
    /// sumiria em silêncio (o bug voltaria só sob concorrência, sem teste vermelho).
    /// </summary>
    [Fact]
    public async Task Handle_NaoDeveVazarAMarcaDeReentranciaParaOChamador()
    {
        using var semaforo = new SemaforoDoCircuito();
        var behavior = new SerializacaoDbContextBehavior<MensagemFake, int>(semaforo);

        await behavior.Handle(new MensagemFake(), (_, _) => new ValueTask<int>(1), CancellationToken.None);

        // Liberou o portão ao terminar.
        Assert.Equal(1, semaforo.Semaforo.CurrentCount);

        // E o próximo Handle no MESMO fluxo do chamador adquire o portão de novo (CurrentCount 0
        // durante a execução). Se a marca tivesse vazado, este assert falharia.
        var portaoAdquirido = false;
        await behavior.Handle(
            new MensagemFake(),
            (_, _) =>
            {
                portaoAdquirido = semaforo.Semaforo.CurrentCount == 0;
                return new ValueTask<int>(2);
            },
            CancellationToken.None);

        Assert.True(portaoAdquirido);
    }

    /// <summary>
    /// Se o handler lançar, o portão tem de ser liberado — senão a primeira falha travaria o
    /// circuito inteiro (todo <c>Send</c> seguinte ficaria esperando para sempre).
    /// </summary>
    [Fact]
    public async Task Handle_DeveLiberarOPortao_QuandoOHandlerLanca()
    {
        using var semaforo = new SemaforoDoCircuito();
        var behavior = new SerializacaoDbContextBehavior<MensagemFake, int>(semaforo);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await behavior.Handle(
                new MensagemFake(),
                (_, _) => throw new InvalidOperationException("falha simulada"),
                CancellationToken.None));

        Assert.Equal(1, semaforo.Semaforo.CurrentCount);

        // O circuito continua utilizável depois da falha.
        var resultado = await behavior.Handle(
            new MensagemFake(),
            (_, _) => new ValueTask<int>(7),
            CancellationToken.None);

        Assert.Equal(7, resultado);
    }

    /// <summary>Mensagem mínima para exercitar o behavior sem depender de um comando de domínio.</summary>
    public sealed record MensagemFake : IMessage;
}