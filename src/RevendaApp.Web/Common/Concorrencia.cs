namespace RevendaApp.Web.Common;

/// <summary>
/// Contrato de mensagem para conflitos de concorrência otimista. As entidades <c>Produto</c> e
/// <c>Cliente</c> têm token <c>xmin</c> (row version do Postgres), então qualquer gravação sobre
/// uma linha alterada por outra operação falha com <c>DbUpdateConcurrencyException</c>.
/// </summary>
/// <remarks>
/// <b>Fonte única da mensagem.</b> O <c>ConcorrenciaBehavior</c> traduz a exceção do EF para
/// <see cref="Mensagem"/>; as telas exibem o texto da <c>InvalidOperationException</c> resultante.
/// Não duplique o literal em handlers ou componentes.
/// </remarks>
public static class Concorrencia
{
    /// <summary>
    /// Mensagem exibida ao operador quando outra operação alterou o mesmo produto/cliente entre a
    /// leitura e a gravação (venda simultânea do último item, pagamento concorrente). Recarregar e
    /// repetir é a ação correta.
    /// </summary>
    public const string Mensagem =
        "Os dados deste produto ou cliente foram alterados por outra operação enquanto você finalizava. "
        + "Recarregue a tela e tente novamente.";
}