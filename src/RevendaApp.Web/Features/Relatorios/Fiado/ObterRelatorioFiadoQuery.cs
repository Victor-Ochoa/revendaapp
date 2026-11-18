using Mediator;

using RevendaApp.Domain.Enums;
using RevendaApp.Web.Common.Relatorios;

namespace RevendaApp.Web.Features.Relatorios.Fiado;

/// <summary>Saldo a receber por cliente, faixas de atraso e recebimentos do mês.</summary>
public sealed record ObterRelatorioFiadoQuery(PeriodoMes Periodo) : IQuery<RelatorioFiadoResult>;

public sealed record RelatorioFiadoResult(
    PeriodoMes Periodo,
    decimal TotalAReceber,
    IReadOnlyList<FaixaAging> Faixas,
    IReadOnlyList<ClienteDevedorItem> Clientes,
    IReadOnlyList<RecebimentoDoDia> RecebimentosPorDia,
    IReadOnlyList<RecebimentoPorForma> RecebimentosPorForma,
    decimal TotalRecebido);

/// <summary>Uma faixa de atraso (0–30, 31–60, 61–90, 90+).</summary>
public sealed record FaixaAging(string Rotulo, int Clientes, decimal Valor);

/// <summary>
/// Um cliente devedor. <see cref="Saldo"/> é o <c>SaldoDevedor</c> do cadastro (o total a
/// receber); <see cref="TemDivergenciaDeSaldo"/> avisa quando ele difere da soma do
/// <c>SaldoFiado</c> das vendas em aberto (ajuste manual no cadastro).
/// </summary>
public sealed record ClienteDevedorItem(
    Guid ClienteId,
    string Nome,
    string Telefone,
    decimal Saldo,
    int DiasAtraso,
    DateTime? VendaMaisAntigaEmAberto,
    bool TemDivergenciaDeSaldo);

public sealed record RecebimentoDoDia(int Dia, decimal Valor);

public sealed record RecebimentoPorForma(FormaPagamento Forma, decimal Valor);