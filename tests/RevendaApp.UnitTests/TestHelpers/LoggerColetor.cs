using Microsoft.Extensions.Logging;

namespace RevendaApp.UnitTests.TestHelpers;

/// <summary>
/// <see cref="ILogger{T}"/> que guarda as entradas em memória, para os testes verificarem
/// <b>comportamento</b> de log (nível, mensagem, exceção e placeholders estruturados) em vez de
/// apenas a assinatura do construtor.
/// </summary>
public sealed class LoggerColetor<T> : ILogger<T>
{
    public List<EntradaDeLog> Entradas { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var valores = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
        Entradas.Add(new EntradaDeLog(
            logLevel,
            formatter(state, exception),
            exception,
            valores.ToDictionary(par => par.Key, par => par.Value)));
    }
}

/// <summary>Uma linha de log capturada por <see cref="LoggerColetor{T}"/>.</summary>
public sealed record EntradaDeLog(
    LogLevel Nivel,
    string Mensagem,
    Exception? Excecao,
    IReadOnlyDictionary<string, object?> Propriedades);