namespace RevendaApp.Domain.Enums;

/// <summary>
/// De onde veio a movimentação de estoque. É ortogonal a <see cref="TipoMovimentacaoEstoque"/>,
/// que diz a direção (entrada/saída/ajuste): a origem diz o motivo.
/// </summary>
/// <remarks>
/// A distinção existe porque "entrada" não é sinônimo de "compra": o estorno de uma venda editada
/// ou excluída também devolve itens ao estoque, e somá-lo às compras infla o total comprado. Quem
/// quer responder "quanto comprei da distribuidora" filtra por <see cref="Compra"/>, não por
/// <c>Tipo == Entrada</c>.
/// </remarks>
public enum OrigemMovimentacaoEstoque
{
    /// <summary>Entrada manual no estoque — na prática, compra na distribuidora.</summary>
    Compra,

    /// <summary>Saída gerada por uma venda.</summary>
    Venda,

    /// <summary>Entrada que devolve ao estoque os itens de uma venda editada ou excluída.</summary>
    EstornoVenda,

    /// <summary>Saída manual, ajuste de inventário, carga inicial de demonstração.</summary>
    Manual,
}