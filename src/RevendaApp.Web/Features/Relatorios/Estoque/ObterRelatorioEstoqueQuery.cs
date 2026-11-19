using Mediator;

using RevendaApp.Web.Common.Relatorios;

namespace RevendaApp.Web.Features.Relatorios.Estoque;

/// <summary>Entradas e saídas de estoque por produto, dia a dia, num mês civil.</summary>
/// <param name="Periodo">Mês civil de Brasília a relatar.</param>
/// <param name="Filtro">
/// Quais movimentos exibir. O default <see cref="FiltroMovimentoEstoque.Ambos"/> preserva o
/// comportamento anterior à existência do filtro. Em qualquer modo, o estorno de venda editada ou
/// excluída fica de fora: ele devolve itens ao estoque, mas não é compra nem saída comercial.
/// </param>
public sealed record ObterRelatorioEstoqueQuery(
    PeriodoMes Periodo,
    FiltroMovimentoEstoque Filtro = FiltroMovimentoEstoque.Ambos) : IQuery<RelatorioEstoqueResult>;

/// <summary>Quais movimentos o relatório de estoque exibe.</summary>
public enum FiltroMovimentoEstoque
{
    /// <summary>Entradas e saídas (comportamento padrão).</summary>
    Ambos,

    /// <summary>Só as entradas — na prática, as compras na distribuidora.</summary>
    Entradas,

    /// <summary>Só as saídas — na prática, as vendas.</summary>
    Saidas,
}

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