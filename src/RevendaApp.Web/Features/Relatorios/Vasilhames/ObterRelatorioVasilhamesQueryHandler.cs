using Mediator;

using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Enums;
using RevendaApp.Infrastructure;
using RevendaApp.Infrastructure.Tenancy;
using RevendaApp.Web.Common;

namespace RevendaApp.Web.Features.Relatorios.Vasilhames;

/// <summary>
/// Relatório de vasilhames: empréstimos e devoluções do mês e o que segue na rua.
/// </summary>
/// <remarks>
/// As devoluções vêm do <c>HistoricoEstoque</c> (tipo <c>DevolucaoVasilhame</c>), não do
/// <c>EmprestimoVasilhame.DevolvidoEm</c>: uma devolução parcial não quita o empréstimo e por isso
/// não deixa data de quitação — só o histórico registra cada devolução individualmente.
/// </remarks>
public sealed class ObterRelatorioVasilhamesQueryHandler(AppDbContext db, ITenantContext tenant)
    : IQueryHandler<ObterRelatorioVasilhamesQuery, RelatorioVasilhamesResult>
{
    public async ValueTask<RelatorioVasilhamesResult> Handle(
        ObterRelatorioVasilhamesQuery query, CancellationToken cancellationToken)
    {
        // Exige empresa selecionada (produtos, empréstimos e histórico vêm pelo filtro global).
        _ = tenant.EmpresaIdRequerida("obter relatório de vasilhames");

        var periodo = query.Periodo;

        var produtos = await db.Produtos
            .AsNoTracking()
            .Where(p => p.ControlaVasilhame)
            .OrderBy(p => p.Nome)
            .ToListAsync(cancellationToken);

        // Em aberto: agrupado por cliente+produto. Carrega o cliente/produto para o nome exibido.
        var emprestimosAbertos = await db.EmprestimosVasilhames
            .AsNoTracking()
            .Include(e => e.Cliente)
            .Include(e => e.Produto)
            .Where(e => e.DevolvidoEm == null)
            .ToListAsync(cancellationToken);

        var emprestimosDoMes = await db.EmprestimosVasilhames
            .AsNoTracking()
            .Where(e => e.Data >= periodo.InicioUtc && e.Data < periodo.FimUtc)
            .ToListAsync(cancellationToken);

        var devolucoes = await db.HistoricosEstoque
            .AsNoTracking()
            .Where(h => h.Tipo == TipoHistoricoEstoque.DevolucaoVasilhame
                        && h.Data >= periodo.InicioUtc
                        && h.Data < periodo.FimUtc)
            .ToListAsync(cancellationToken);

        var emAberto = emprestimosAbertos
            .GroupBy(e => new { e.ClienteId, e.ProdutoId })
            .Select(g => new EmprestimoEmAbertoItem(
                g.Key.ClienteId,
                g.First().Cliente?.Nome ?? "",
                g.Key.ProdutoId,
                g.First().Produto?.Nome ?? "",
                g.Sum(e => e.Saldo),
                g.Min(e => e.Data)))
            .Where(e => e.Saldo > 0)
            .OrderByDescending(e => e.Saldo)
            .ThenBy(e => e.ClienteNome)
            .ToList();

        var porProduto = produtos
            .Select(produto => new VasilhamePorProduto(
                produto.Id,
                produto.Nome,
                emprestimosDoMes.Where(e => e.ProdutoId == produto.Id).Sum(e => e.Quantidade),
                devolucoes.Where(h => h.ProdutoId == produto.Id).Sum(h => h.Quantidade ?? 0),
                emAberto.Where(e => e.ProdutoId == produto.Id).Sum(e => e.Saldo)))
            .ToList();

        // Agrupar em memória: o EF não traduz TimeZoneInfo.
        var devolucoesPorDia = Enumerable.Range(1, periodo.DiasNoMes)
            .Select(dia => new DevolucaoDoDia(
                dia,
                devolucoes
                    .Where(h => BrasilTimeZone.ParaBrasilia(h.Data).Day == dia)
                    .Sum(h => h.Quantidade ?? 0)))
            .ToList();

        return new RelatorioVasilhamesResult(
            periodo,
            porProduto,
            emAberto,
            devolucoesPorDia,
            emprestimosDoMes.Sum(e => e.Quantidade),
            devolucoes.Sum(h => h.Quantidade ?? 0));
    }
}