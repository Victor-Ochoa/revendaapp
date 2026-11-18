using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;

namespace RevendaApp.Web.Common;

/// <summary>
/// Regra única de lucro reconhecido: venda paga no ato reconhece tudo; fiado reconhece na
/// proporção do que já foi pago (e integral quando quitado). Usada pelo dashboard e pelos
/// relatórios para não existirem duas verdades.
/// </summary>
public static class VendaLucro
{
    public static decimal Reconhecido(Venda venda)
    {
        if (venda.FormaPagamento != FormaPagamento.Fiado)
        {
            return venda.LucroTotal;
        }

        if (venda.ValorTotal <= 0m)
        {
            return 0m;
        }

        return venda.LucroTotal * (venda.ValorPagoFiado / venda.ValorTotal);
    }

    public static decimal Reconhecido(IEnumerable<Venda> vendas) => vendas.Sum(Reconhecido);
}