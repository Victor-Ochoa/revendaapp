using RevendaApp.Domain.Enums;

namespace RevendaApp.Domain.Entities;

/// <summary>
/// Registro de entrada, saída ou ajuste de estoque de um produto.
/// </summary>
public class MovimentacaoEstoque
{
    public Guid Id { get; private set; }

    /// <summary>Empresa (tenant) dona desta movimentação.</summary>
    public Guid EmpresaId { get; private set; }

    public Guid ProdutoId { get; private set; }
    public Produto? Produto { get; private set; }
    public TipoMovimentacaoEstoque Tipo { get; private set; }

    /// <summary>
    /// Motivo da movimentação. Ortogonal a <see cref="Tipo"/>: uma Entrada pode ser
    /// <see cref="OrigemMovimentacaoEstoque.Compra"/> (compra na distribuidora) ou
    /// <see cref="OrigemMovimentacaoEstoque.EstornoVenda"/> (venda editada/excluída devolvendo
    /// itens ao estoque) — e só a primeira conta como compra nos relatórios.
    /// </summary>
    public OrigemMovimentacaoEstoque Origem { get; private set; }

    public int Quantidade { get; private set; }
    public DateTime Data { get; private set; }
    public string? Motivo { get; private set; }

    private MovimentacaoEstoque()
    {
    }

    public MovimentacaoEstoque(Guid produtoId, TipoMovimentacaoEstoque tipo, int quantidade,
        string? motivo = null, DateTime? data = null,
        OrigemMovimentacaoEstoque origem = OrigemMovimentacaoEstoque.Manual)
    {
        if (quantidade <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantidade), "A quantidade movimentada deve ser maior que zero.");
        }

        Id = Guid.NewGuid();
        ProdutoId = produtoId;
        Tipo = tipo;
        Origem = origem;
        Quantidade = quantidade;
        Motivo = motivo;
        Data = data ?? DateTime.UtcNow;
    }
}