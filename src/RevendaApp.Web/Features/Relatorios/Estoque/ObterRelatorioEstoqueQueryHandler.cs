using Mediator;

using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Enums;
using RevendaApp.Infrastructure;
using RevendaApp.Infrastructure.Tenancy;
using RevendaApp.Web.Common;

namespace RevendaApp.Web.Features.Relatorios.Estoque;

/// <summary>
/// Monta a matriz produto × dia do mês com entradas e saídas. Os ajustes ficam fora da matriz
/// (não são movimento: registram um novo total) e são listados à parte.
/// </summary>
public sealed class ObterRelatorioEstoqueQueryHandler(AppDbContext db, ITenantContext tenant)
    : IQueryHandler<ObterRelatorioEstoqueQuery, RelatorioEstoqueResult>
{
    public async ValueTask<RelatorioEstoqueResult> Handle(
        ObterRelatorioEstoqueQuery query, CancellationToken cancellationToken)
    {
        // Exige empresa selecionada (produtos e movimentos vêm pelo filtro global do tenant).
        _ = tenant.EmpresaIdRequerida("obter relatório de estoque");

        var periodo = query.Periodo;

        // Todos os produtos, inclusive inativos: o relatório é de auditoria, não de venda.
        var produtos = await db.Produtos
            .AsNoTracking()
            .OrderBy(p => p.Nome)
            .ToListAsync(cancellationToken);

        var movimentos = await db.MovimentacoesEstoque
            .AsNoTracking()
            .Where(m => m.Data >= periodo.InicioUtc && m.Data < periodo.FimUtc)
            .ToListAsync(cancellationToken);

        // Agrupar depois de materializar: o EF não traduz TimeZoneInfo, então o dia civil de
        // Brasília é calculado em memória.
        var porProduto = movimentos
            .GroupBy(m => m.ProdutoId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var ajustes = new List<AjusteNoMes>();
        var produtosNoMes = new List<ProdutoNoMes>(produtos.Count);

        foreach (var produto in produtos)
        {
            var entradas = new int[periodo.DiasNoMes];
            var saidas = new int[periodo.DiasNoMes];
            var quantidadeAjustes = 0;

            if (porProduto.TryGetValue(produto.Id, out var doProduto))
            {
                foreach (var movimento in doProduto)
                {
                    var dia = BrasilTimeZone.ParaBrasilia(movimento.Data).Day;
                    var indice = dia - 1;

                    switch (movimento.Tipo)
                    {
                        case TipoMovimentacaoEstoque.Entrada:
                            entradas[indice] += movimento.Quantidade;
                            break;
                        case TipoMovimentacaoEstoque.Saida:
                            saidas[indice] += movimento.Quantidade;
                            break;
                        case TipoMovimentacaoEstoque.Ajuste:
                            // Ajuste não é movimento: o número registrado é o novo total, não um
                            // delta. Somá-lo à matriz inflaria entradas/saídas.
                            quantidadeAjustes++;
                            ajustes.Add(new AjusteNoMes(
                                produto.Id, produto.Nome, dia, movimento.Quantidade, movimento.Motivo));
                            break;
                    }
                }
            }

            var totalEntradas = entradas.Sum();
            var totalSaidas = saidas.Sum();

            produtosNoMes.Add(new ProdutoNoMes(
                produto.Id,
                produto.Nome,
                produto.Ativo,
                entradas,
                saidas,
                totalEntradas,
                totalSaidas,
                totalEntradas - totalSaidas,
                produto.QuantidadeEstoque,
                quantidadeAjustes));
        }

        var porDia = new List<MovimentoDoDia>(periodo.DiasNoMes);
        for (var dia = 1; dia <= periodo.DiasNoMes; dia++)
        {
            var indice = dia - 1;
            porDia.Add(new MovimentoDoDia(
                dia,
                produtosNoMes.Sum(p => p.EntradasPorDia[indice]),
                produtosNoMes.Sum(p => p.SaidasPorDia[indice])));
        }

        return new RelatorioEstoqueResult(periodo, produtosNoMes, ajustes, porDia);
    }
}