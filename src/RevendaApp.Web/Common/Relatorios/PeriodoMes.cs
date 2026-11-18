using System.Globalization;

namespace RevendaApp.Web.Common.Relatorios;

/// <summary>
/// Um mês civil de Brasília, com o intervalo UTC correspondente. Fonte única para os relatórios:
/// nenhum handler monta o intervalo na mão.
/// </summary>
public readonly record struct PeriodoMes
{
    public PeriodoMes(int ano, int mes)
    {
        if (mes is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(mes), "O mês deve estar entre 1 e 12.");
        }

        Ano = ano;
        Mes = mes;
    }

    public int Ano { get; }
    public int Mes { get; }

    /// <summary>Primeiro instante do mês em UTC (dia 1, 00:00 de Brasília).</summary>
    public DateTime InicioUtc => BrasilTimeZone.ParaUtc(new DateTime(Ano, Mes, 1));

    /// <summary>Primeiro instante do mês seguinte em UTC — fim <b>exclusivo</b> do intervalo.</summary>
    public DateTime FimUtc => BrasilTimeZone.ParaUtc(new DateTime(Ano, Mes, 1).AddMonths(1));

    public int DiasNoMes => DateTime.DaysInMonth(Ano, Mes);

    public string Rotulo => $"{NomeDoMes}/{Ano}";

    private string NomeDoMes => new DateTime(Ano, Mes, 1)
        .ToString("MMMM", CultureInfo.GetCultureInfo("pt-BR"));

    /// <summary>Mês civil de Brasília a que um instante UTC pertence.</summary>
    public static PeriodoMes DoDia(DateTime utc)
    {
        var brasilia = BrasilTimeZone.ParaBrasilia(utc);
        return new PeriodoMes(brasilia.Year, brasilia.Month);
    }

    public PeriodoMes Anterior() => Mes == 1 ? new PeriodoMes(Ano - 1, 12) : new PeriodoMes(Ano, Mes - 1);

    public PeriodoMes Proximo() => Mes == 12 ? new PeriodoMes(Ano + 1, 1) : new PeriodoMes(Ano, Mes + 1);
}