using Mediator;

using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.Infrastructure;
using RevendaApp.Infrastructure.Tenancy;
using RevendaApp.Web.Common;

namespace RevendaApp.Web.Features.Relatorios.Fiado;

/// <summary>
/// Relatório de fiado: quem deve, há quanto tempo e o que foi recebido no mês.
/// O atraso é medido pela venda em aberto mais antiga de cada cliente.
/// </summary>
public sealed class ObterRelatorioFiadoQueryHandler(
    AppDbContext db, ITenantContext tenant, TimeProvider timeProvider)
    : IQueryHandler<ObterRelatorioFiadoQuery, RelatorioFiadoResult>
{
    /// <summary>Faixas de atraso, com o limite superior em dias (inclusive). <c>null</c> = sem limite.</summary>
    private static readonly (string Rotulo, int? Ate)[] FaixasDeAtraso =
    [
        ("0–30", 30),
        ("31–60", 60),
        ("61–90", 90),
        ("90+", null),
    ];

    public async ValueTask<RelatorioFiadoResult> Handle(
        ObterRelatorioFiadoQuery query, CancellationToken cancellationToken)
    {
        // Exige empresa selecionada (clientes, vendas e pagamentos vêm pelo filtro global).
        _ = tenant.EmpresaIdRequerida("obter relatório de fiado");

        var periodo = query.Periodo;
        var hojeBrasilia = BrasilTimeZone.ParaBrasilia(timeProvider.GetUtcNow().UtcDateTime).Date;

        // Passada 1: clientes devedores. As duas consultas abaixo cobrem todos os clientes de uma
        // vez — nada de consultar vendas dentro do laço (N+1).
        var devedores = await db.Clientes
            .AsNoTracking()
            .Where(c => c.SaldoDevedor > 0)
            .OrderBy(c => c.Nome)
            .ToListAsync(cancellationToken);

        // Passada 2: todas as vendas fiado em aberto, de uma vez, agrupadas em memória.
        var vendasEmAberto = (await db.Vendas
                .AsNoTracking()
                .Include(v => v.Itens)
                .Where(v => v.FormaPagamento == FormaPagamento.Fiado
                            && v.Status == VendaStatus.FiadoAberto
                            && v.ExcluidaEm == null)
                .ToListAsync(cancellationToken))
            .GroupBy(v => v.ClienteId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var clientes = new List<ClienteDevedorItem>(devedores.Count);
        var faixas = FaixasDeAtraso.ToDictionary(f => f.Rotulo, _ => (Clientes: 0, Valor: 0m));

        foreach (var cliente in devedores)
        {
            vendasEmAberto.TryGetValue(cliente.Id, out var vendasDoCliente);
            vendasDoCliente ??= [];

            var maisAntiga = vendasDoCliente.Count > 0
                ? vendasDoCliente.MinBy(v => v.DataHora)
                : null;

            var diasAtraso = maisAntiga is null
                ? 0
                : (hojeBrasilia - BrasilTimeZone.ParaBrasilia(maisAntiga.DataHora).Date).Days;

            // A divergência é entre o saldo do cadastro e a soma do que as vendas ainda devem.
            var divergencia = vendasDoCliente.Sum(v => v.SaldoFiado) != cliente.SaldoDevedor;

            var rotulo = RotuloDaFaixa(diasAtraso);
            var atual = faixas[rotulo];
            faixas[rotulo] = (atual.Clientes + 1, atual.Valor + cliente.SaldoDevedor);

            clientes.Add(new ClienteDevedorItem(
                cliente.Id,
                cliente.Nome,
                cliente.Telefone,
                cliente.SaldoDevedor,
                diasAtraso,
                maisAntiga?.DataHora,
                divergencia));
        }

        // Recebimentos do mês: Pagamento com VendaId == null é abatimento de fiado (com VendaId é
        // pagamento no ato de uma venda não-fiado e não pertence a este relatório).
        var recebimentos = await db.Pagamentos
            .AsNoTracking()
            .Where(p => p.VendaId == null
                        && p.Data >= periodo.InicioUtc
                        && p.Data < periodo.FimUtc)
            .ToListAsync(cancellationToken);

        var recebimentosPorDia = Enumerable.Range(1, periodo.DiasNoMes)
            .Select(dia => new RecebimentoDoDia(
                dia,
                recebimentos
                    .Where(p => BrasilTimeZone.ParaBrasilia(p.Data).Day == dia)
                    .Sum(p => p.Valor)))
            .ToList();

        var recebimentosPorForma = recebimentos
            .GroupBy(p => p.FormaPagamento)
            .OrderByDescending(g => g.Sum(p => p.Valor))
            .Select(g => new RecebimentoPorForma(g.Key, g.Sum(p => p.Valor)))
            .ToList();

        return new RelatorioFiadoResult(
            periodo,
            devedores.Sum(c => c.SaldoDevedor),
            FaixasDeAtraso.Select(f => new FaixaAging(f.Rotulo, faixas[f.Rotulo].Clientes, faixas[f.Rotulo].Valor)).ToList(),
            clientes,
            recebimentosPorDia,
            recebimentosPorForma,
            recebimentos.Sum(p => p.Valor));
    }

    private static string RotuloDaFaixa(int diasAtraso) =>
        FaixasDeAtraso.First(f => f.Ate is null || diasAtraso <= f.Ate).Rotulo;
}