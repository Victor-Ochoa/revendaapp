namespace RevendaApp.UnitTests.TestHelpers;

/// <summary>
/// <see cref="TimeProvider"/> de teste com instante fixo: permite afirmar que o handler gravou
/// exatamente o instante injetado (auditoria determinística) em vez do relógio do sistema.
/// </summary>
public sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
{
    /// <summary>Instante UTC devolvido por <see cref="GetUtcNow"/>.</summary>
    public DateTime UtcNow { get; } = utcNow;

    public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
}