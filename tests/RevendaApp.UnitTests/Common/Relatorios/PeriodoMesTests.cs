using RevendaApp.Web.Common.Relatorios;

namespace RevendaApp.UnitTests.Common.Relatorios;

public class PeriodoMesTests
{
    [Fact]
    public void PeriodoMes_calcula_intervalo_utc_do_mes_de_brasilia()
    {
        var periodo = new PeriodoMes(2026, 11);

        // 1º de novembro 00:00 BRT = 03:00 UTC; 1º de dezembro 00:00 BRT = 03:00 UTC (exclusivo).
        Assert.Equal(new DateTime(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc), periodo.InicioUtc);
        Assert.Equal(new DateTime(2026, 12, 1, 3, 0, 0, DateTimeKind.Utc), periodo.FimUtc);
    }

    [Theory]
    [InlineData(2026, 11, 30)]
    [InlineData(2026, 2, 28)]
    [InlineData(2028, 2, 29)]
    [InlineData(2026, 4, 30)]
    [InlineData(2026, 12, 31)]
    public void PeriodoMes_calcula_dias_do_mes(int ano, int mes, int esperado)
    {
        Assert.Equal(esperado, new PeriodoMes(ano, mes).DiasNoMes);
    }

    [Fact]
    public void PeriodoMes_Dezembro_vira_janeiro_ao_avancar()
    {
        var proximo = new PeriodoMes(2026, 12).Proximo();
        Assert.Equal(new PeriodoMes(2027, 1), proximo);
    }

    [Fact]
    public void PeriodoMes_Janeiro_volta_para_dezembro_do_ano_anterior()
    {
        Assert.Equal(new PeriodoMes(2025, 12), new PeriodoMes(2026, 1).Anterior());
    }

    [Fact]
    public void PeriodoMes_DoDia_usa_o_mes_civil_de_brasilia()
    {
        // 01/12/2026 01:00 UTC ainda é 30/11/2026 22:00 em Brasília.
        var periodo = PeriodoMes.DoDia(new DateTime(2026, 12, 1, 1, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new PeriodoMes(2026, 11), periodo);
    }

    [Theory]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    public void PeriodoMes_rejeita_mes_fora_de_1_a_12(int ano, int mes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeriodoMes(ano, mes));
    }

    [Fact]
    public void PeriodoMes_tem_rotulo_em_pt_br()
    {
        Assert.Equal("novembro/2026", new PeriodoMes(2026, 11).Rotulo);
    }
}