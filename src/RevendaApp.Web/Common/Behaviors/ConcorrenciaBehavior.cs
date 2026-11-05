using Mediator;

using Microsoft.EntityFrameworkCore;

using RevendaApp.Infrastructure;

namespace RevendaApp.Web.Common.Behaviors;

/// <summary>
/// Pipeline técnico e transversal do Mediator que traduz <see cref="DbUpdateConcurrencyException"/>
/// (token <c>xmin</c> de <c>Produto</c>/<c>Cliente</c>) numa <see cref="InvalidOperationException"/>
/// com mensagem amigável, em vez de deixar a exceção do EF subir e derrubar o circuito Blazor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que <see cref="DbContext.ChangeTracker"/> é limpo:</b> o <see cref="AppDbContext"/> é
/// <c>scoped</c> ao circuito Blazor Server. O EF <b>não</b> desfaz o estado das entidades quando um
/// <c>SaveChangesAsync</c> falha — a <c>Venda</c>, o <c>Pagamento</c>, as movimentações de estoque
/// e o <c>MarcarFiadoQuitado</c> da tentativa que falhou continuam pendentes no tracker. As telas
/// (<c>NovaVendaDialog</c>, <c>ClienteDetalhe</c>) apenas exibem a mensagem e mantêm o modal aberto,
/// <b>não</b> recriam o circuito. Sem a limpeza, a próxima operação do operador no mesmo circuito
/// commitaria esse lote fantasma num save aparentemente não relacionado — exatamente a corrupção
/// que o token de concorrência existe para evitar. <b>Não remova esta limpeza.</b>
/// </para>
/// <para>
/// A exceção original é preservada como <c>InnerException</c> para não perder o diagnóstico.
/// Não usamos <c>TransacaoComRetry</c>: a execution strategy do Npgsql não reexecuta
/// <see cref="DbUpdateConcurrencyException"/> (não é erro transitório), e repetir exigiria
/// reconstruir o estado da operação.
/// </para>
/// </remarks>
public sealed class ConcorrenciaBehavior<TMessage, TResponse>(AppDbContext db)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    public async ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        try
        {
            return await next(message, cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Descarta TODAS as entidades pendentes da tentativa que falhou (ver o XML doc da
            // classe: sem isto, a próxima operação do circuito commita o lote fantasma).
            db.ChangeTracker.Clear();
            throw new InvalidOperationException(Concorrencia.Mensagem, ex);
        }
    }
}