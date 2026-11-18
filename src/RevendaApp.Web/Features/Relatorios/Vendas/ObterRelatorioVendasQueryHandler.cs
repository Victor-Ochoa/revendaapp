using Mediator;

using Microsoft.EntityFrameworkCore;

using RevendaApp.Infrastructure;
using RevendaApp.Infrastructure.Tenancy;
using RevendaApp.Web.Common;

namespace RevendaApp.Web.Features.Relatorios.Vendas;

/// <summary>
/// Agrega as vendas do mês por dia (faturamento, lucro, desconto) e por forma de pagamento.
/// O lucro usa <see cref="VendaLucro"/> — a mesma regra do dashboard, para não haver duas verdades.
/// </summary>
public sealed class ObterRelatorioVendasQueryHandler(AppDbContext db, ITenantContext tenant)
    : IQueryHandler<ObterRelatorioVendasQuery, RelatorioVendasResult>
{
    public async ValueTask<RelatorioVendasResult> Handle(
        ObterRelatorioVendasQuery query, CancellationToken cancellationToken)
    {
        // Exige empresa selecionada (vendas vêm pelo filtro global do tenant).
        _ = tenant.EmpresaIdRequerida("obter relatório de vendas");

        var periodo = query.Periodo;

        // ValorTotal/LucroTotal não são colunas: os itens precisam vir carregados.
        var vendas = await db.Vendas
            .AsNoTracking()
            .Include(v => v.Itens)
            .Where(v => v.ExcluidaEm == null
                        && v.DataHora >= periodo.InicioUtc
                        && v.DataHora < periodo.FimUtc)
            .ToListAsync(cancellationToken);

        // Agrupar em memória: o EF não traduz TimeZoneInfo.
        var porDia = vendas
            .GroupBy(v => BrasilTimeZone.ParaBrasilia(v.DataHora).Day)
            .OrderBy(g => g.Key)
            .Select(g => new VendaDoDia(
                g.Key,
                g.Count(),
                g.Sum(v => v.ValorTotal),
                VendaLucro.Reconhecido(g),
                g.Sum(v => v.Desconto)))
            .ToList();

        var porForma = vendas
            .GroupBy(v => v.FormaPagamento)
            .OrderByDescending(g => g.Sum(v => v.ValorTotal))
            .Select(g => new VendaPorFormaNoMes(g.Key, g.Count(), g.Sum(v => v.ValorTotal)))
            .ToList();

        var faturamentoTotal = vendas.Sum(v => v.ValorTotal);
        var quantidade = vendas.Count;

        return new RelatorioVendasResult(
            periodo,
            porDia,
            porForma,
            faturamentoTotal,
            VendaLucro.Reconhecido(vendas),
            vendas.Sum(v => v.Desconto),
            quantidade,
            quantidade == 0 ? 0m : faturamentoTotal / quantidade);
    }
}