using Mediator;

using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Enums;
using RevendaApp.Infrastructure;
using RevendaApp.Infrastructure.Tenancy;
using RevendaApp.Web.Common;
using RevendaApp.Web.Common.Authorization;

namespace RevendaApp.Web.Features.Admin.Vendas;

public sealed class ExcluirVendaCommandHandler(
    AppDbContext db,
    ITenantContext tenant,
    TimeProvider timeProvider,
    IUsuarioAutenticado? usuarioAutenticado = null,
    VendasAtualizadasNotifier? notifier = null)
    : ICommandHandler<ExcluirVendaCommand, ExcluirVendaResult>
{
    public async ValueTask<ExcluirVendaResult> Handle(ExcluirVendaCommand command, CancellationToken cancellationToken)
    {
        // Exige empresa selecionada (venda localizada pelo filtro global do tenant).
        _ = tenant.EmpresaIdRequerida("excluir venda");

        // Restrito a administradores (reforço no servidor).
        if (usuarioAutenticado is not null)
        {
            await usuarioAutenticado.RequerAdminAsync(cancellationToken);
        }

        var venda = await db.Vendas
            .Include(v => v.Itens)
            .FirstOrDefaultAsync(v => v.Id == command.VendaId, cancellationToken)
            ?? throw new InvalidOperationException("A venda a excluir não foi encontrada.");

        if (venda.ExcluidaEm is not null)
        {
            throw new InvalidOperationException("A venda já foi excluída.");
        }

        // Fiado com QUALQUER valor já pago não pode ser excluído: o estorno devolveria só o SaldoFiado
        // e o dinheiro recebido ficaria órfão, sem a venda que o justificava.
        if (venda.FormaPagamento == FormaPagamento.Fiado && venda.ValorPagoFiado > 0m)
        {
            throw new InvalidOperationException(
                "Não é possível excluir: esta venda fiado já teve pagamento e o estorno não devolveria o valor recebido.");
        }

        // Estorna saldo devedor (fiado) e estoque; remove pagamento de venda paga no ato.
        await ReversaoVendaHelper.ReverterAsync(db, venda, cancellationToken);

        // Exclusão lógica: a venda permanece no banco (não quebrar FKs) mas deixa de contar.
        venda.Excluir(timeProvider.GetUtcNow().UtcDateTime);

        await db.SaveChangesAsync(cancellationToken);

        // Recalcula a recorrência de recompra dos produtos com aviso após a exclusão persistir
        // (o motor consulta o banco e só enxerga a venda marcada como excluída depois do save acima).
        foreach (var item in venda.Itens)
        {
            var produto = await db.Produtos.FindAsync([item.ProdutoId], cancellationToken);
            if (produto is { AvisarRecompra: true })
            {
                await RecorrenciaCompraAprendizado.RecalcularAsync(db, venda.ClienteId, item.ProdutoId, cancellationToken);
            }
        }

        // Segundo save para persistir as linhas de RecorrenciasCompra criadas/atualizadas/removidas.
        await db.SaveChangesAsync(cancellationToken);

        if (notifier is not null)
        {
            await notifier.NotificarVendaRegistrada();
        }

        return new ExcluirVendaResult(venda.Id, Sucesso: true);
    }
}