namespace RevendaApp.Domain.Entities;

/// <summary>
/// Fonte da verdade de "quantos vasilhames vazios o cliente ainda deve/devolveu" para um
/// determinado produto. Um empréstimo nasce com <see cref="Quantidade"/>; conforme o cliente
/// devolve, <see cref="QuantidadeDevolvida"/> cresce até quitar (<see cref="DevolvidoEm"/>).
/// </summary>
public class EmprestimoVasilhame
{
    public Guid Id { get; private set; }

    /// <summary>Empresa (tenant) dona deste empréstimo.</summary>
    public Guid EmpresaId { get; private set; }

    public Guid ClienteId { get; private set; }
    public Cliente? Cliente { get; private set; }
    public Guid ProdutoId { get; private set; }
    public Produto? Produto { get; private set; }

    /// <summary>
    /// Venda que originou o empréstimo (quando o empréstimo nasce de uma venda com a opção
    /// "é empréstimo"). Opcional: empréstimos manuais (tela do cliente/estoque) não têm venda.
    /// Usado pela reversão de venda para desfazer exatamente o que a venda criou.
    /// </summary>
    public Guid? VendaId { get; private set; }
    public Venda? Venda { get; private set; }

    /// <summary>Quantidade de vasilhames emprestados.</summary>
    public int Quantidade { get; private set; }

    /// <summary>Data do empréstimo.</summary>
    public DateTime Data { get; private set; }

    /// <summary>Observação opcional do empréstimo.</summary>
    public string? Observacao { get; private set; }

    /// <summary>Quantidade de vasilhames já devolvidos pelo cliente.</summary>
    public int QuantidadeDevolvida { get; private set; }

    /// <summary>Data em que o empréstimo foi totalmente quitado (null enquanto houver saldo).</summary>
    public DateTime? DevolvidoEm { get; private set; }

    /// <summary>Quantidade de vasilhames ainda devolvidos.</summary>
    public int Saldo => Quantidade - QuantidadeDevolvida;

    /// <summary>Construtor exigido pelo EF Core.</summary>
    private EmprestimoVasilhame()
    {
    }

    public EmprestimoVasilhame(Guid clienteId, Guid produtoId, int quantidade, DateTime? data = null, string? observacao = null, Guid? vendaId = null)
    {
        if (quantidade <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantidade), "A quantidade de vasilhames emprestados deve ser maior que zero.");
        }

        Id = Guid.NewGuid();
        ClienteId = clienteId;
        ProdutoId = produtoId;
        Quantidade = quantidade;
        Data = data ?? DateTime.UtcNow;
        Observacao = observacao;
        VendaId = vendaId;
        QuantidadeDevolvida = 0;
        DevolvidoEm = null;
    }

    /// <summary>
    /// Registra a devolução de vasilhames emprestados. Devoluções parciais aumentam
    /// <see cref="QuantidadeDevolvida"/> e reduzem <see cref="Saldo"/>, sem marcar quitação;
    /// a devolução que quita integralmente o empréstimo define <see cref="DevolvidoEm"/>.
    /// </summary>
    /// <param name="quantidade">Quantidade de vasilhames devolvidos nesta operação.</param>
    /// <param name="agora">
    /// Instante da devolução, gravado apenas quando o empréstimo é quitado integralmente. O handler
    /// passa o <c>TimeProvider</c> para manter a auditoria coerente e testável; omitido, usa
    /// <see cref="DateTime.UtcNow"/>.
    /// </param>
    public void RegistrarDevolucao(int quantidade, DateTime? agora = null)
    {
        if (quantidade <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantidade), "A quantidade de vasilhames devolvidos deve ser maior que zero.");
        }

        if (quantidade > Saldo)
        {
            throw new ArgumentOutOfRangeException(nameof(quantidade), $"A devolução excede o saldo do empréstimo: saldo {Saldo}, devolução {quantidade}.");
        }

        QuantidadeDevolvida += quantidade;

        if (QuantidadeDevolvida == Quantidade)
        {
            DevolvidoEm = agora ?? DateTime.UtcNow;
        }
    }
}