using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.Web.Common;

namespace RevendaApp.UnitTests.Common;

public class VendaLucroTests
{
    private static Venda Venda(FormaPagamento forma, decimal preco = 100m, decimal custo = 60m)
    {
        var venda = new Venda(Guid.NewGuid(), forma);
        venda.AdicionarItem(Guid.NewGuid(), 1, preco, custo);
        return venda;
    }

    [Fact]
    public void Venda_paga_no_ato_reconhece_lucro_integral()
    {
        Assert.Equal(40m, VendaLucro.Reconhecido(Venda(FormaPagamento.Pix)));
    }

    [Fact]
    public void Fiado_em_aberto_nao_reconhece_lucro()
    {
        Assert.Equal(0m, VendaLucro.Reconhecido(Venda(FormaPagamento.Fiado)));
    }

    [Fact]
    public void Fiado_pago_pela_metade_reconhece_metade_do_lucro()
    {
        var venda = Venda(FormaPagamento.Fiado);
        venda.AbaterFiado(50m);

        Assert.Equal(20m, VendaLucro.Reconhecido(venda));
    }

    [Fact]
    public void Fiado_quitado_reconhece_lucro_integral()
    {
        var venda = Venda(FormaPagamento.Fiado);
        venda.AbaterFiado(100m);

        Assert.Equal(40m, VendaLucro.Reconhecido(venda));
    }

    [Fact]
    public void Soma_reconhecido_de_varias_vendas()
    {
        var pix = Venda(FormaPagamento.Pix);
        var fiado = Venda(FormaPagamento.Fiado);
        fiado.AbaterFiado(50m);

        Assert.Equal(60m, VendaLucro.Reconhecido([pix, fiado]));
    }
}