using Mediator;

using RevendaApp.Domain.Enums;
using RevendaApp.Web.Common.Relatorios;

namespace RevendaApp.Web.Features.Relatorios.Vendas;

/// <summary>Faturamento, lucro, desconto e ticket do mês, por dia e por forma de pagamento.</summary>
public sealed record ObterRelatorioVendasQuery(PeriodoMes Periodo) : IQuery<RelatorioVendasResult>;

public sealed record RelatorioVendasResult(
    PeriodoMes Periodo,
    IReadOnlyList<VendaDoDia> PorDia,
    IReadOnlyList<VendaPorFormaNoMes> PorForma,
    decimal FaturamentoTotal,
    decimal LucroTotal,
    decimal DescontoTotal,
    int QuantidadeVendas,
    decimal TicketMedio);

/// <summary>Um dia do mês. O lucro segue <c>VendaLucro.Reconhecido</c> (fiado só na proporção paga).</summary>
public sealed record VendaDoDia(int Dia, int Quantidade, decimal Faturamento, decimal Lucro, decimal Desconto);

public sealed record VendaPorFormaNoMes(FormaPagamento Forma, int Quantidade, decimal Valor);