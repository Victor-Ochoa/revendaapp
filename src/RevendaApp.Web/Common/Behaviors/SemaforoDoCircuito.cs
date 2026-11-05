using System.Threading;

namespace RevendaApp.Web.Common.Behaviors;

/// <summary>
/// Portão único de acesso ao <c>AppDbContext</c> scoped do circuito Blazor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que existe:</b> o EF Core proíbe duas operações simultâneas no mesmo
/// <c>DbContext</c> (<c>InvalidOperationException</c>: "A second operation was started on this
/// context instance"). O <c>AppDbContext</c> é scoped ao circuito e compartilhado por todos os
/// componentes dele — dois componentes que consultam no <c>OnInitializedAsync</c> colidem, porque o
/// render do filho acontece durante o <c>await</c> do pai. Foi assim que a aba "Minha Empresa"
/// (que consulta) derrubou <c>/configuracoes</c> quando passou a ser a aba ativa inicial.
/// </para>
/// <para>
/// <b>Por que um semáforo e não uma "fila" de contexto:</b> trocar por um contexto por operação
/// quebraria as invariantes de tenancy do projeto — o filtro global fecha sobre
/// <c>ITenantContext.EmpresaId</c> e o change tracker precisa ser o mesmo entre a validação e o
/// save do handler. Serializar preserva o contexto único e o isolamento por empresa.
/// </para>
/// <para>
/// <b>Reentrância:</b> um handler pode disparar outro <c>Send</c> na mesma cadeia
/// (<c>RegistrarPagamentoCommandHandler</c> → <c>VendasAtualizadasNotifier</c> →
/// <c>Dashboard.RecarregarAsync</c>). Esse fluxo já segura o portão; esperar por ele seria
/// <b>deadlock</b>. A marca de reentrância usa <see cref="AsyncLocal{T}"/> de propósito: ela
/// acompanha a cadeia assíncrona até o fim do <c>Send</c> e volta ao valor do chamador quando ele
/// termina — por isso a operação <b>seguinte</b> do mesmo componente volta a passar pelo portão
/// (uma marca em campo do serviço scoped ficaria ligada e o portão sumiria em silêncio).
/// </para>
/// <para>
/// <b>Limite conhecido:</b> <see cref="AsyncLocal{T}"/> é herdado por qualquer trabalho iniciado
/// durante o <c>await</c> do handler. Na prática isso não afrouxa o portão: o Blazor não dispara o
/// ciclo de vida de outro componente a partir do fluxo de um handler — o render acontece na cadeia
/// do próprio componente. O caso que originou o bug (página e aba consultando no
/// <c>OnInitializedAsync</c>) é pego, e o teste de integração
/// <c>ConfiguracoesCarregamentoTests</c> falha se o portão for removido.
/// </para>
/// </remarks>
public sealed class SemaforoDoCircuito : IDisposable
{
    private readonly SemaphoreSlim _semaforo = new(1, 1);
    private readonly AsyncLocal<bool> _dentroDoFluxo = new();

    /// <summary>Semáforo que serializa as operações; exposto para o behavior e para os testes.</summary>
    internal SemaphoreSlim Semaforo => _semaforo;

    /// <summary>Indica que o fluxo assíncrono atual já está dentro do portão (reentrância).</summary>
    internal bool DentroDoFluxo
    {
        get => _dentroDoFluxo.Value;
        set => _dentroDoFluxo.Value = value;
    }

    /// <inheritdoc />
    public void Dispose() => _semaforo.Dispose();
}