using Mediator;

using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.Infrastructure;
using RevendaApp.Infrastructure.Tenancy;
using RevendaApp.Web.Common;

namespace RevendaApp.Web.Features.Clientes.Pagar;

public sealed class RegistrarPagamentoCommandHandler(AppDbContext db, ITenantContext tenant, VendasAtualizadasNotifier notifier)
    : ICommandHandler<RegistrarPagamentoCommand, RegistrarPagamentoResult>
{
    /// <summary>
    /// Mensagem exibida quando outra operação alterou o mesmo produto/cliente entre a leitura e a
    /// gravação (venda simultânea do último item, pagamento concorrente). A tradução da exceção é
    /// feita pelo <c>ConcorrenciaBehavior</c>; esta constante apenas expõe o texto compartilhado.
    /// </summary>
    public const string MensagemConflito = Concorrencia.Mensagem;

    public async ValueTask<RegistrarPagamentoResult> Handle(RegistrarPagamentoCommand command, CancellationToken cancellationToken)
    {
        // Exige empresa selecionada (cliente/vendas localizados pelo filtro global do tenant).
        _ = tenant.EmpresaIdRequerida("registrar pagamento");

        var cliente = await db.Clientes.FindAsync([command.ClienteId], cancellationToken)
            ?? throw new InvalidOperationException($"Cliente '{command.ClienteId}' não encontrado.");

        var pagamento = new Pagamento(cliente.Id, command.Valor, command.FormaPagamento, command.Observacao);
        db.Pagamentos.Add(pagamento);

        cliente.RegistrarPagamento(command.Valor);

        // Quita as vendas fiado mais antigas primeiro (FIFO), até o valor pago cobrir o saldo.
        var vendasFiado = await db.Vendas
            .Where(v => v.ClienteId == cliente.Id
                        && v.ExcluidaEm == null
                        && v.FormaPagamento == FormaPagamento.Fiado
                        && v.Status == VendaStatus.FiadoAberto)
            .OrderBy(v => v.DataHora)
            .Include(v => v.Itens)
            .ToListAsync(cancellationToken);

        var valorPago = command.Valor;
        foreach (var venda in vendasFiado)
        {
            if (valorPago <= 0)
            {
                break;
            }

            // FIFO estrito: a venda mais antiga em aberto precisa ser coberta inteira antes de
            // qualquer venda posterior ser marcada como quitada. Sem este `break`, um pagamento
            // parcial que não fecha a dívida antiga "pulava" para a próxima venda e a marcava
            // FiadoQuitado — reconhecendo lucro de uma venda ainda não paga no dashboard, além de
            // deixar a venda mais antiga em aberto com a mais nova quitada.
            if (valorPago < venda.ValorTotal)
            {
                break;
            }

            venda.MarcarFiadoQuitado();
            valorPago -= venda.ValorTotal;
        }

        // O token `xmin` do Cliente detecta pagamento concorrente sobre o mesmo saldo devedor; o
        // `ConcorrenciaBehavior` traduz o conflito e limpa o change tracker.
        await db.SaveChangesAsync(cancellationToken);
        await notifier.NotificarVendaRegistrada();

        return new RegistrarPagamentoResult(pagamento.Id, cliente.SaldoDevedor);
    }
}