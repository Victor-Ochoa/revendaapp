using Mediator;

using Microsoft.EntityFrameworkCore;

using RevendaApp.Infrastructure;
using RevendaApp.Infrastructure.Tenancy;
using RevendaApp.Web.Common;
using RevendaApp.Web.Features.Clientes.Emprestimos;

namespace RevendaApp.Web.Features.Clientes.Detalhe;

public sealed class ObterClienteDetalheQueryHandler(AppDbContext db, ITenantContext tenant, TimeProvider? timeProvider = null)
    : IQueryHandler<ObterClienteDetalheQuery, ClienteDetalheResult?>
{
    public async ValueTask<ClienteDetalheResult?> Handle(ObterClienteDetalheQuery query, CancellationToken cancellationToken)
    {
        // Exige empresa selecionada (cliente/vendas localizados pelo filtro global do tenant).
        _ = tenant.EmpresaIdRequerida("obter detalhe do cliente");

        var cliente = await db.Clientes.FindAsync([query.ClienteId], cancellationToken);
        if (cliente is null)
        {
            return null;
        }

        // Reaproveita o agrupamento de empréstimos em aberto do slice de Empréstimos (Task 8).
        var emprestimosHandler = new ObterEmprestimosClienteQueryHandler(db, tenant);
        var emprestimos = await emprestimosHandler.Handle(
            new ObterEmprestimosClienteQuery(query.ClienteId),
            cancellationToken);

        var vendas = await db.Vendas
            .Where(v => v.ClienteId == query.ClienteId && v.ExcluidaEm == null)
            .Include(v => v.Itens)
                .ThenInclude(i => i.Produto)
            .OrderByDescending(v => v.DataHora)
            .ToListAsync(cancellationToken);

        var historico = vendas
            .Select(v => new VendaHistoricoItem(v.Id, BrasilTimeZone.ParaBrasilia(v.DataHora), v.FormaPagamento, v.Status, v.ValorTotal, v.LucroTotal, v.ValorEntrega))
            .ToList();

        // Agrega por produto a quantidade total comprada (todas as vendas não excluídas,
        // incluindo itens empréstimo). Só afeta a exibição na tela do cliente.
        var produtosComprados = vendas
            .SelectMany(v => v.Itens)
            .GroupBy(i => new ProdutoCompradoKey(i.ProdutoId, i.Produto?.Nome ?? "Produto removido"))
            .OrderByDescending(g => g.Sum(i => i.Quantidade))
            .ThenBy(g => g.Key.Nome, StringComparer.Ordinal)
            .Select(g => new ProdutoCompradoItem(g.Key.ProdutoId, g.Key.Nome, g.Sum(i => i.Quantidade)))
            .ToList();

        var pagamentos = await db.Pagamentos
            .Where(p => p.ClienteId == query.ClienteId)
            .OrderByDescending(p => p.Data)
            .ToListAsync(cancellationToken);

        var historicoPagamentos = pagamentos
            .Select(p => new PagamentoHistoricoItem(p.Id, BrasilTimeZone.ParaBrasilia(p.Data), p.Valor, p.FormaPagamento, p.Observacao))
            .ToList();

        // Recompra atual por produto com aviso: intervalo aprendido (dias) e data prevista da
        // próxima compra, calculados em dias de calendário de Brasília (mesma regra do dashboard).
        var agora = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var hojeBrasilia = BrasilTimeZone.ParaBrasilia(agora).Date;
        var recompras = await db.RecorrenciasCompra
            .Where(r => r.ClienteId == query.ClienteId
                && r.IntervaloMedioDias > 0
                && r.Produto != null && r.Produto.AvisarRecompra)
            .Include(r => r.Produto)
            .ToListAsync(cancellationToken);

        var recomprasCliente = recompras
            .Where(r => r.Produto is not null)
            .Select(r => new
            {
                r,
                UltimaCompraBrasilia = BrasilTimeZone.ParaBrasilia(r.DataUltimaCompra).Date,
                IntervaloDias = (double)r.IntervaloMedioDias,
            })
            .OrderBy(x => x.UltimaCompraBrasilia.AddDays(x.IntervaloDias))
            .Select(x => new RecompraClienteItem(
                x.r.Produto!.Id,
                x.r.Produto!.Nome,
                BrasilTimeZone.ParaBrasilia(x.r.DataUltimaCompra),
                x.r.IntervaloMedioDias,
                x.UltimaCompraBrasilia.AddDays(x.IntervaloDias),
                (int)(hojeBrasilia - x.UltimaCompraBrasilia.AddDays(x.IntervaloDias)).TotalDays,
                x.r.QuantidadeCompras))
            .ToList();

        return new ClienteDetalheResult(
            cliente.Id,
            cliente.Nome,
            cliente.Telefone,
            cliente.Endereco,
            cliente.SaldoDevedor,
            cliente.Ativo,
            historico,
            historicoPagamentos,
            emprestimos,
            produtosComprados,
            recomprasCliente);
    }

    private sealed record ProdutoCompradoKey(Guid ProdutoId, string Nome);
}