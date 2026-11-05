using Mediator;

namespace RevendaApp.Web.Common.Behaviors;

/// <summary>
/// Serializa o pipeline do Mediator por circuito, para que duas operações nunca usem o
/// <c>AppDbContext</c> scoped ao mesmo tempo.
/// </summary>
/// <remarks>
/// <para>
/// <b>O defeito que isto corrige:</b> abrir <c>/configuracoes</c> derrubava a aba "Minha Empresa"
/// com <see cref="InvalidOperationException"/> ("A second operation was started on this context
/// instance"). A página consulta no <c>OnInitializedAsync</c>; enquanto esse <c>await</c> está em
/// voo, o Blazor renderiza e o componente da aba ativa dispara a própria consulta — as duas no
/// mesmo <c>AppDbContext</c> do circuito.
/// </para>
/// <para>
/// <b>Por que no pipeline e não no componente:</b> o problema é do contexto compartilhado, não de
/// uma tela. Qualquer par de componentes que consulte no ciclo de vida colide (a central tem o
/// mesmo padrão página + aba), e validators também consultam o banco. O portão aqui cobre o
/// pipeline inteiro — validação, handler e os <c>Send</c> que o handler dispara.
/// </para>
/// <para>
/// <b>Posição na ordem:</b> este é o behavior <b>mais externo</b> (o primeiro registrado), então o
/// <see cref="ConcorrenciaBehavior{TMessage,TResponse}"/> roda dentro dele. Consequência
/// pretendida: o <c>ChangeTracker.Clear()</c> do conflito acontece antes de o portão ser liberado,
/// e a próxima operação do circuito nunca enxerga estado pendente da tentativa que falhou.
/// </para>
/// </remarks>
public sealed class SerializacaoDbContextBehavior<TMessage, TResponse>(SemaforoDoCircuito semaforo)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        // Reentrância: o fluxo já segura o portão (handler → notifier → query). Esperar aqui
        // travaria o circuito.
        if (semaforo.DentroDoFluxo)
        {
            return await next(message, cancellationToken);
        }

        await semaforo.Semaforo.WaitAsync(cancellationToken);
        semaforo.DentroDoFluxo = true;
        try
        {
            return await next(message, cancellationToken);
        }
        finally
        {
            // Zerar (e não "restaurar"): só se chega aqui quem adquiriu o portão, e a marca é
            // sempre false antes disso. O `AsyncLocal` faz a marca voltar ao valor do chamador
            // sozinho ao sair do fluxo — é por isso que a próxima operação do componente não a
            // herda (ver o teste Handle_NaoDeveVazarAMarcaDeReentranciaParaOChamador).
            semaforo.DentroDoFluxo = false;
            semaforo.Semaforo.Release();
        }
    }
}