using Mediator;

using RevendaApp.Web.Common.Relatorios;

namespace RevendaApp.Web.Features.Relatorios.Estoque;

/// <summary>Entradas e saídas de estoque por produto, dia a dia, num mês civil.</summary>
public sealed record ObterRelatorioEstoqueQuery(PeriodoMes Periodo) : IQuery<RelatorioEstoqueResult>;

public sealed record RelatorioEstoqueResult(
    PeriodoMes Periodo,
    IReadOnlyList<ProdutoNoMes> Produtos,
    IReadOnlyList<AjusteNoMes> Ajustes,
    IReadOnlyList<MovimentoDoDia> PorDia);

/// <summary>
/// Movimento de um produto no mês. Os vetores têm um item por dia (índice 0 = dia 1).
/// <see cref="MovimentoLiquido"/> é entrada − saída no mês; <see cref="EstoqueAtual"/> é o
/// estoque de hoje, não o do mês (ajustes não são reconstruíveis).
/// </summary>
public sealed record ProdutoNoMes(
    Guid ProdutoId,
    string Nome,
    bool Ativo,
    IReadOnlyList<int> EntradasPorDia,
    IReadOnlyList<int> SaidasPorDia,
    int TotalEntradas,
    int TotalSaidas,
    int MovimentoLiquido,
    int EstoqueAtual,
    int QuantidadeAjustes);

/// <summary>Ajuste de estoque do mês: registra o <b>novo total</b>, não o delta.</summary>
public sealed record AjusteNoMes(Guid ProdutoId, string ProdutoNome, int Dia, int NovoTotal, string? Motivo);

public sealed record MovimentoDoDia(int Dia, int Entradas, int Saidas);