using RevendaApp.Domain.Enums;

namespace RevendaApp.Domain.Entities;

/// <summary>
/// Registro de uma venda: itens vendidos, forma de pagamento, desconto e valor de entrega.
/// O desconto é único por venda e é rateado proporcionalmente entre os itens para
/// calcular o lucro líquido de cada um. O valor de entrega é único por venda, entra no
/// faturamento (valor total) e não compõe o lucro.
/// </summary>
public class Venda
{
    private readonly List<ItemVenda> _itens = [];

    public Guid Id { get; private set; }

    /// <summary>Empresa (tenant) dona desta venda.</summary>
    public Guid EmpresaId { get; private set; }

    public Guid ClienteId { get; private set; }
    public Cliente? Cliente { get; private set; }
    public DateTime DataHora { get; private set; }
    public FormaPagamento FormaPagamento { get; private set; }
    public decimal Desconto { get; private set; }

    /// <summary>
    /// Valor cobrado pela entrega do pedido, único por venda (começa zerado). Soma ao valor
    /// total, entra no faturamento (Fiado soma ao saldo devedor; demais formas pagam no ato)
    /// e não compõe o lucro.
    /// </summary>
    public decimal ValorEntrega { get; private set; }

    /// <summary>Situação de pagamento da venda, persistida no banco.</summary>
    public VendaStatus Status { get; private set; }

    /// <summary>Id do usuário autenticado que registrou a venda (apenas registro, sem FK).</summary>
    public Guid? VendedorId { get; private set; }

    /// <summary>
    /// Data/hora em que a venda foi excluída (exclusão lógica). Vendas excluídas permanecem no
    /// banco para não quebrar referências, mas deixam de contar nas queries e no dashboard,
    /// além de estornarem saldo devedor (fiado) e estoque.
    /// </summary>
    public DateTime? ExcluidaEm { get; private set; }

    public IReadOnlyCollection<ItemVenda> Itens => _itens.AsReadOnly();

    /// <summary>Soma dos itens antes do desconto.</summary>
    public decimal ValorBruto => _itens.Sum(i => i.ValorTotal);

    /// <summary>
    /// Valor final da venda, líquido do desconto e somado da entrega — é o que soma ao saldo
    /// devedor quando Fiado (entrega incluída).
    /// </summary>
    public decimal ValorTotal => ValorBruto - Desconto + ValorEntrega;

    /// <summary>Soma do lucro líquido (já descontado) de todos os itens.</summary>
    public decimal LucroTotal => _itens.Sum(i => i.Lucro);

    private Venda()
    {
    }

    public Venda(Guid clienteId, FormaPagamento formaPagamento, DateTime? dataHora = null)
    {
        Id = Guid.NewGuid();
        ClienteId = clienteId;
        FormaPagamento = formaPagamento;
        DataHora = dataHora ?? DateTime.UtcNow;
        Desconto = 0m;
        ValorEntrega = 0m;
        Status = formaPagamento == FormaPagamento.Fiado ? VendaStatus.FiadoAberto : VendaStatus.Pago;
    }

    /// <summary>Registra o usuário que realizou a venda (apenas registro, não altera regra de negócio).</summary>
    public void DefinirVendedor(Guid? vendedorId) => VendedorId = vendedorId;

    /// <summary>
    /// Marca a venda como excluída (soft delete). A primeira chamada registra o momento; chamadas
    /// seguintes são inócuas (preservam o registro original para auditoria).
    /// </summary>
    /// <param name="agora">
    /// Instante da exclusão. O handler passa o <c>TimeProvider</c> para manter a auditoria coerente e
    /// testável; omitido, usa <see cref="DateTime.UtcNow"/>.
    /// </param>
    public void Excluir(DateTime? agora = null) => ExcluidaEm ??= agora ?? DateTime.UtcNow;

    /// <summary>
    /// Marca uma venda fiado como quitada (usada quando um pagamento do cliente abate o saldo
    /// devedor). Não tem efeito em vendas que não sejam fiado.
    /// </summary>
    public void MarcarFiadoQuitado()
    {
        if (FormaPagamento == FormaPagamento.Fiado && Status == VendaStatus.FiadoAberto)
        {
            Status = VendaStatus.FiadoQuitado;
        }
    }

    public ItemVenda AdicionarItem(Guid produtoId, int quantidade, decimal precoUnitario, decimal precoCustoUnitario, bool ehEmprestimo = false)
    {
        var item = new ItemVenda(Id, produtoId, quantidade, precoUnitario, precoCustoUnitario, ehEmprestimo);
        _itens.Add(item);
        RatearDesconto();
        return item;
    }

    /// <summary>
    /// Define (ou substitui) o desconto único da venda, começando em zero, e recalcula a
    /// parcela rateada proporcionalmente ao valor de cada item.
    /// </summary>
    public void AplicarDesconto(decimal desconto)
    {
        if (desconto < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desconto), "O desconto não pode ser negativo.");
        }

        if (desconto > ValorBruto)
        {
            throw new ArgumentOutOfRangeException(nameof(desconto), "O desconto não pode ser maior que o valor bruto da venda.");
        }

        Desconto = desconto;
        RatearDesconto();
    }

    /// <summary>
    /// Define (ou substitui) o valor de entrega único da venda, começando em zero. Entra no
    /// valor total (faturamento) mas não compõe o lucro, pois não é um item.
    /// </summary>
    public void DefinirValorEntrega(decimal valorEntrega)
    {
        if (valorEntrega < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(valorEntrega), "O valor da entrega não pode ser negativo.");
        }

        ValorEntrega = valorEntrega;
    }

    private void RatearDesconto()
    {
        if (_itens.Count == 0)
        {
            return;
        }

        var valorBruto = ValorBruto;
        if (valorBruto == 0)
        {
            foreach (var item in _itens)
            {
                item.AplicarDescontoRateado(0m);
            }

            return;
        }

        decimal acumulado = 0m;
        for (var i = 0; i < _itens.Count; i++)
        {
            var item = _itens[i];
            decimal parcela;
            if (i == _itens.Count - 1)
            {
                // O último item absorve o resíduo de arredondamento, para o total bater exatamente.
                parcela = Desconto - acumulado;
            }
            else
            {
                parcela = Math.Round(Desconto * (item.ValorTotal / valorBruto), 2, MidpointRounding.AwayFromZero);
                acumulado += parcela;
            }

            item.AplicarDescontoRateado(parcela);
        }
    }
}