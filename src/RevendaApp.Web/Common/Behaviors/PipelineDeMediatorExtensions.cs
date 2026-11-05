using Mediator;

using Microsoft.Extensions.DependencyInjection;

namespace RevendaApp.Web.Common.Behaviors;

/// <summary>
/// Registro único dos <see cref="IPipelineBehavior{TMessage,TResponse}"/> técnicos do Mediator.
/// </summary>
/// <remarks>
/// <para>
/// <b>A ordem é o contrato.</b> O Mediator monta o encadeamento a partir do
/// <c>IEnumerable&lt;IPipelineBehavior&lt;,&gt;&gt;</c> resolvido do DI <b>invertendo</b> a lista
/// (<c>pipelineBehaviours.Reverse()</c> no código gerado), então o <b>primeiro</b> registrado é o
/// <b>mais externo</b>.
/// </para>
/// <para>
/// <see cref="SerializacaoDbContextBehavior{TMessage,TResponse}"/> é o mais externo de todos: ele
/// precisa envolver a validação e o handler porque <b>os dois</b> consultam o banco, e o
/// <c>AppDbContext</c> é um só por circuito. Ficando por fora, o
/// <see cref="ConcorrenciaBehavior{TMessage,TResponse}"/> roda dentro do portão — assim o
/// <c>ChangeTracker.Clear()</c> acontece antes de o portão ser liberado e a próxima operação do
/// circuito nunca enxerga estado pendente da tentativa que falhou.
/// </para>
/// <para>
/// O <see cref="ConcorrenciaBehavior{TMessage,TResponse}"/> fica por fora da validação de
/// propósito: o conflito de concorrência só nasce no save do handler e precisa envolver o pipeline
/// inteiro para limpar o change tracker em qualquer handler que mute Produto/Cliente.
/// <b>Não inverta estas linhas</b> — <c>ConcorrenciaBehaviorTests</c> pina a ordem resolvida.
/// </remarks>
public static class PipelineDeMediatorExtensions
{
    /// <summary>Registra os behaviors técnicos na ordem que define o pipeline (ver remarks).</summary>
    public static IServiceCollection AddPipelineBehaviors(this IServiceCollection services)
    {
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(SerializacaoDbContextBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ConcorrenciaBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        return services;
    }
}