namespace RevendaApp.UnitTests.TestHelpers;

/// <summary>TimeProvider com instante fixo, para testes de "hoje" sem depender do relógio.</summary>
public sealed class TimeProviderFixo(DateTimeOffset agora) : TimeProvider
{
    public static TimeProviderFixo Em(DateTime utc) => new(new DateTimeOffset(utc, TimeSpan.Zero));

    public override DateTimeOffset GetUtcNow() => agora;
}