using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.Infrastructure;

namespace RevendaApp.Web.Features.Fiado.Normalizacao;

/// <summary>Resumo da normalização, para log e auditoria.</summary>
public sealed record ResultadoNormalizacao(int ClientesAnalisados, int VendasNormalizadas, int Divergencias);

/// <summary>
/// Reconstrói <see cref="Venda.ValorPagoFiado"/> das vendas fiado anteriores à coluna, a partir
/// do histórico imutável: pagamentos com <c>VendaId == null</c> são abatimentos de saldo devedor
/// e são replicados em FIFO sobre as vendas em aberto de cada cliente.
/// </summary>
/// <remarks>
/// <para>
/// Roda no bootstrap, uma única vez, marcada por <see cref="RegistroAuditoria.Acoes.FiadoLegadoNormalizado"/>.
/// É idempotente e não sobrescreve <see cref="Cliente.SaldoDevedor"/>: divergências são
/// registradas, não corrigidas à força.
/// </para>
/// <para>
/// <b>Recebe a fábrica de contextos por empresa</b> porque o bootstrap resolve o
/// <see cref="AppDbContext"/> em modo central, e nesse modo o <c>SetEmpresaIdInterceptor</c> lança
/// ao gravar entidade de domínio. A escrita acontece sempre num contexto tenant-bound; o
/// <c>dbCentral</c> serve só para a leitura cross-tenant dos ids de empresa e para o marcador
/// global de auditoria.
/// </para>
/// </remarks>
public static class NormalizarFiadoLegado
{
    public static async Task<ResultadoNormalizacao> ExecutarAsync(
        AppDbContext dbCentral,
        Func<Guid, AppDbContext> criarContextoDaEmpresa,
        CancellationToken cancellationToken)
    {
        var jaExecutou = await dbCentral.RegistrosAuditoria
            .AnyAsync(r => r.Acao == RegistroAuditoria.Acoes.FiadoLegadoNormalizado, cancellationToken);

        if (jaExecutou)
        {
            return new ResultadoNormalizacao(0, 0, 0);
        }

        // Passada 1 (leitura, modo central): quais empresas têm venda fiado. Empresas, Vendas e
        // Clientes no modo central não filtram, então esta leitura enxerga todas de uma vez.
        var empresas = await dbCentral.Vendas
            .Where(v => v.FormaPagamento == FormaPagamento.Fiado)
            .Select(v => v.EmpresaId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var clientesAnalisados = 0;
        var vendasNormalizadas = 0;
        var divergencias = 0;
        var detalhesDivergencia = new List<string>();

        // Passada 2 (escrita): um contexto tenant-bound por empresa. É obrigatório — o interceptor
        // lança ao gravar domínio em modo central, e o filtro global é o que garante que esta
        // passada nunca misture dados de duas empresas.
        foreach (var empresaId in empresas)
        {
            await using var db = criarContextoDaEmpresa(empresaId);

            var clientes = await db.Clientes.ToListAsync(cancellationToken);
            clientesAnalisados += clientes.Count;

            var vendasPorCliente = (await db.Vendas
                    .Where(v => v.FormaPagamento == FormaPagamento.Fiado && v.ExcluidaEm == null)
                    .OrderBy(v => v.DataHora)
                    .Include(v => v.Itens)
                    .ToListAsync(cancellationToken))
                .GroupBy(v => v.ClienteId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var pagamentosPorCliente = (await db.Pagamentos
                    .Where(p => p.VendaId == null)
                    .OrderBy(p => p.Data)
                    .ToListAsync(cancellationToken))
                .GroupBy(p => p.ClienteId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var cliente in clientes)
            {
                if (!vendasPorCliente.TryGetValue(cliente.Id, out var vendas) || vendas.Count == 0)
                {
                    continue;
                }

                // Replay determinístico: a soma dos pagamentos de fiado (VendaId null) é consumida
                // em FIFO pelas vendas em aberto. Alvo calculado por venda, depois um set absoluto —
                // assim rodar de novo produz exatamente o mesmo estado.
                var acumulado = pagamentosPorCliente.TryGetValue(cliente.Id, out var pagamentos)
                    ? pagamentos.Sum(p => p.Valor)
                    : 0m;

                foreach (var venda in vendas)
                {
                    var alocado = Math.Min(acumulado, venda.ValorTotal);
                    acumulado -= alocado;

                    if (venda.ValorPagoFiado != alocado)
                    {
                        venda.DefinirValorPagoFiado(alocado);
                        vendasNormalizadas++;
                    }
                }

                var somaSaldoVendas = vendas.Sum(v => v.SaldoFiado);
                if (somaSaldoVendas != cliente.SaldoDevedor)
                {
                    divergencias++;
                    detalhesDivergencia.Add(
                        $"{empresaId}/{cliente.Id}: cadastro {cliente.SaldoDevedor:F2}, vendas {somaSaldoVendas:F2}");
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        var resumo = $"Clientes analisados: {clientesAnalisados}; vendas normalizadas: {vendasNormalizadas}; " +
                     $"divergências: {divergencias}";
        if (detalhesDivergencia.Count > 0)
        {
            resumo += "; " + string.Join(" | ", detalhesDivergencia);
        }

        // O marcador é global (RegistroAuditoria não tem filtro): grava no contexto central.
        dbCentral.RegistrosAuditoria.Add(new RegistroAuditoria(
            empresaId: null,
            usuarioId: null,
            usuarioNome: null,
            RegistroAuditoria.Acoes.FiadoLegadoNormalizado,
            resumo,
            RegistroAuditoria.Origens.Sistema));

        await dbCentral.SaveChangesAsync(cancellationToken);

        return new ResultadoNormalizacao(clientesAnalisados, vendasNormalizadas, divergencias);
    }
}