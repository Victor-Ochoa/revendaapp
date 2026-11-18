using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;

namespace RevendaApp.UnitTests.Domain;

/// <summary>
/// A quitação de fiado é por venda: cada venda guarda quanto já foi pago
/// (<see cref="Venda.ValorPagoFiado"/>) e quanto ainda falta (<see cref="Venda.SaldoFiado"/>).
/// Antes disso só o saldo total do cliente existia, e um pagamento parcial não deixava rastro
/// em nenhuma venda — o lucro nunca era reconhecido e o aging mentia.
/// </summary>
public class VendaFiadoTests
{
    private static Venda VendaFiado(decimal valor = 100m, DateTime? dataHora = null)
    {
        var venda = new Venda(Guid.NewGuid(), FormaPagamento.Fiado, dataHora);
        venda.AdicionarItem(Guid.NewGuid(), 1, valor, 60m);
        return venda;
    }

    [Fact]
    public void AbaterFiado_aloca_parcialmente_e_mantem_venda_aberta()
    {
        var venda = VendaFiado(100m);

        var alocado = venda.AbaterFiado(40m);

        Assert.Equal(40m, alocado);
        Assert.Equal(40m, venda.ValorPagoFiado);
        Assert.Equal(60m, venda.SaldoFiado);
        Assert.Equal(VendaStatus.FiadoAberto, venda.Status);
    }

    [Fact]
    public void AbaterFiado_quita_quando_alocacao_cobre_o_total()
    {
        var venda = VendaFiado(100m);

        venda.AbaterFiado(40m);
        var alocado = venda.AbaterFiado(60m);

        Assert.Equal(60m, alocado);
        Assert.Equal(0m, venda.SaldoFiado);
        Assert.Equal(VendaStatus.FiadoQuitado, venda.Status);
    }

    [Fact]
    public void AbaterFiado_nao_aloca_alem_do_saldo_e_devolve_o_excedente()
    {
        var venda = VendaFiado(100m);

        var alocado = venda.AbaterFiado(150m);

        Assert.Equal(100m, alocado);
        Assert.Equal(100m, venda.ValorPagoFiado);
        Assert.Equal(0m, venda.SaldoFiado);
    }

    [Fact]
    public void AbaterFiado_ignora_venda_que_nao_e_fiado()
    {
        var venda = new Venda(Guid.NewGuid(), FormaPagamento.Pix);
        venda.AdicionarItem(Guid.NewGuid(), 1, 100m, 60m);

        var alocado = venda.AbaterFiado(50m);

        Assert.Equal(0m, alocado);
        Assert.Equal(0m, venda.ValorPagoFiado);
    }

    [Fact]
    public void AbaterFiado_ignora_venda_ja_quitada()
    {
        var venda = VendaFiado(100m);
        venda.AbaterFiado(100m);

        var alocado = venda.AbaterFiado(10m);

        Assert.Equal(0m, alocado);
        Assert.Equal(100m, venda.ValorPagoFiado);
    }

    [Fact]
    public void AbaterFiado_nao_altera_venda_excluida()
    {
        var venda = VendaFiado(100m);
        venda.Excluir();

        var alocado = venda.AbaterFiado(50m);

        Assert.Equal(0m, alocado);
        Assert.Equal(0m, venda.ValorPagoFiado);
    }

    [Fact]
    public void AbaterFiado_ignora_valor_nao_positivo()
    {
        var venda = VendaFiado(100m);

        Assert.Equal(0m, venda.AbaterFiado(0m));
        Assert.Equal(0m, venda.AbaterFiado(-10m));
        Assert.Equal(0m, venda.ValorPagoFiado);
    }

    [Fact]
    public void MarcarFiadoQuitado_equivale_a_abater_o_saldo_restante()
    {
        var venda = VendaFiado(100m);
        venda.AbaterFiado(30m);

        venda.MarcarFiadoQuitado();

        Assert.Equal(100m, venda.ValorPagoFiado);
        Assert.Equal(0m, venda.SaldoFiado);
        Assert.Equal(VendaStatus.FiadoQuitado, venda.Status);
    }
}