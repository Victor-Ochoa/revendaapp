using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Features.Fiado.Normalizacao;

namespace RevendaApp.UnitTests.Features.Fiado;

public class NormalizarFiadoLegadoTests
{
    /// <summary>
    /// Cria a base InMemory com <b>nome fixo</b> para que a fábrica passada ao normalizador abra
    /// contextos novos na MESMA base. A fábrica não pode devolver este mesmo <c>db</c>: o
    /// normalizador faz <c>await using</c> no contexto que a fábrica entrega e o descartaria no
    /// fim da primeira empresa.
    /// </summary>
    private static async Task<(RevendaApp.Infrastructure.AppDbContext Db, Cliente Cliente, string Banco)> CenarioAsync(
        decimal[] valoresFiado, decimal[] pagamentos)
    {
        var banco = Guid.NewGuid().ToString();
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId, nomeBanco: banco);
        var cliente = new Cliente("Maria", "11988887777", "Rua A, 1");
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        var dia = 1;
        foreach (var valor in valoresFiado)
        {
            var venda = new Venda(cliente.Id, FormaPagamento.Fiado, new DateTime(2026, 1, dia++, 10, 0, 0, DateTimeKind.Utc));
            venda.AdicionarItem(Guid.NewGuid(), 1, valor, valor / 2);
            db.Vendas.Add(venda);
            cliente.AdicionarSaldoDevedor(valor);
        }

        var diaPagamento = 1;
        foreach (var pagamento in pagamentos)
        {
            // VendaId null = abatimento de saldo devedor (fiado).
            db.Pagamentos.Add(new Pagamento(cliente.Id, pagamento, FormaPagamento.Dinheiro,
                data: new DateTime(2026, 2, diaPagamento++, 10, 0, 0, DateTimeKind.Utc)));
            cliente.RegistrarPagamento(pagamento);
        }

        await db.SaveChangesAsync();
        return (db, cliente, banco);
    }

    /// <summary>Fábrica que abre um contexto NOVO na mesma base InMemory do cenário.</summary>
    private static Func<Guid, RevendaApp.Infrastructure.AppDbContext> Fabrica(string banco) =>
        _ => InMemoryDbContextFactory.Create(TenantTeste.EmpresaId, nomeBanco: banco);

    /// <summary>
    /// Contexto de leitura limpo para os asserts. A normalização grava por um contexto próprio,
    /// então o <c>db</c> do cenário tem entidades rastreadas com estado antigo — ler por ele
    /// devolveria o valor de antes da normalização.
    /// </summary>
    private static RevendaApp.Infrastructure.AppDbContext Leitura(string banco) =>
        InMemoryDbContextFactory.Create(TenantTeste.EmpresaId, nomeBanco: banco);

    [Fact]
    public async Task Normalizacao_aloca_pagamentos_parciais_nas_vendas_mais_antigas()
    {
        var (db, _, banco) = await CenarioAsync([60m, 40m], [50m]);

        await NormalizarFiadoLegado.ExecutarAsync(db, Fabrica(banco), CancellationToken.None);

        using var leitura = Leitura(banco);
        // ValorTotal/SaldoFiado são calculados sobre os itens: sem o Include, o agregado sai zero.
        var vendas = leitura.Vendas.Include(v => v.Itens).OrderBy(v => v.DataHora).ToList();
        Assert.Equal(50m, vendas[0].ValorPagoFiado);
        Assert.Equal(10m, vendas[0].SaldoFiado);
        Assert.Equal(0m, vendas[1].ValorPagoFiado);
    }

    [Fact]
    public async Task Normalizacao_quita_venda_quando_a_alocacao_cobre_o_total()
    {
        var (db, _, banco) = await CenarioAsync([60m, 40m], [70m]);

        await NormalizarFiadoLegado.ExecutarAsync(db, Fabrica(banco), CancellationToken.None);

        using var leitura = Leitura(banco);
        var vendas = leitura.Vendas.OrderBy(v => v.DataHora).ToList();
        Assert.Equal(VendaStatus.FiadoQuitado, vendas[0].Status);
        Assert.Equal(10m, vendas[1].ValorPagoFiado);
        Assert.Equal(VendaStatus.FiadoAberto, vendas[1].Status);
    }

    [Fact]
    public async Task Normalizacao_ignora_pagamento_de_venda_no_ato()
    {
        var (db, cliente, banco) = await CenarioAsync([100m], []);
        // Pagamento vinculado a venda (não-fiado) não pode abater fiado.
        db.Pagamentos.Add(new Pagamento(cliente.Id, 100m, FormaPagamento.Pix, vendaId: Guid.NewGuid()));
        await db.SaveChangesAsync();

        await NormalizarFiadoLegado.ExecutarAsync(db, Fabrica(banco), CancellationToken.None);

        using var leitura = Leitura(banco);
        Assert.Equal(0m, leitura.Vendas.Single().ValorPagoFiado);
    }

    [Fact]
    public async Task Normalizacao_ignora_venda_excluida()
    {
        var (db, _, banco) = await CenarioAsync([100m], [100m]);
        db.Vendas.Single().Excluir();
        await db.SaveChangesAsync();

        await NormalizarFiadoLegado.ExecutarAsync(db, Fabrica(banco), CancellationToken.None);

        using var leitura = Leitura(banco);
        Assert.Equal(0m, leitura.Vendas.Single().ValorPagoFiado);
    }

    [Fact]
    public async Task Normalizacao_e_idempotente()
    {
        var (db, _, banco) = await CenarioAsync([60m, 40m], [70m]);

        await NormalizarFiadoLegado.ExecutarAsync(db, Fabrica(banco), CancellationToken.None);
        using var leitura1 = Leitura(banco);
        var primeira = leitura1.Vendas.OrderBy(v => v.DataHora).Select(v => v.ValorPagoFiado).ToList();

        // Remove o marcador e roda de novo: o estado tem de ser o mesmo (set absoluto, não acúmulo).
        var marcador = leitura1.RegistrosAuditoria.Single(r => r.Acao == RegistroAuditoria.Acoes.FiadoLegadoNormalizado);
        leitura1.RegistrosAuditoria.Remove(marcador);
        await leitura1.SaveChangesAsync();

        await NormalizarFiadoLegado.ExecutarAsync(db, Fabrica(banco), CancellationToken.None);
        using var leitura2 = Leitura(banco);
        var segunda = leitura2.Vendas.OrderBy(v => v.DataHora).Select(v => v.ValorPagoFiado).ToList();

        Assert.Equal(primeira, segunda);
    }

    [Fact]
    public async Task Normalizacao_nao_aloca_excedente_de_credito()
    {
        var (db, _, banco) = await CenarioAsync([100m], [150m]);

        await NormalizarFiadoLegado.ExecutarAsync(db, Fabrica(banco), CancellationToken.None);

        using var leitura = Leitura(banco);
        Assert.Equal(100m, leitura.Vendas.Single().ValorPagoFiado);
    }

    [Fact]
    public async Task Normalizacao_nao_roda_quando_o_marcador_ja_existe()
    {
        var (db, _, banco) = await CenarioAsync([100m], [100m]);
        // Marcador já gravado (como numa subida anterior) e venda ainda sem alocação:
        // a segunda execução não pode tocar nada.
        db.RegistrosAuditoria.Add(new RegistroAuditoria(null, null, null,
            RegistroAuditoria.Acoes.FiadoLegadoNormalizado, "resumo", RegistroAuditoria.Origens.Sistema));
        await db.SaveChangesAsync();

        var resultado = await NormalizarFiadoLegado.ExecutarAsync(db, Fabrica(banco), CancellationToken.None);

        Assert.Equal(0, resultado.VendasNormalizadas);
        using var leitura = Leitura(banco);
        Assert.Equal(0m, leitura.Vendas.Single().ValorPagoFiado);
        Assert.Equal(1, leitura.RegistrosAuditoria.Count());
    }

    [Fact]
    public async Task Normalizacao_registra_divergencia_sem_alterar_saldo_do_cliente()
    {
        var (db, cliente, banco) = await CenarioAsync([100m], [40m]);
        // Simula ajuste manual: saldo do cadastro difere da soma das vendas.
        cliente.RegistrarPagamento(50m);
        await db.SaveChangesAsync();

        var resultado = await NormalizarFiadoLegado.ExecutarAsync(db, Fabrica(banco), CancellationToken.None);

        Assert.Equal(1, resultado.Divergencias);
        using var leitura = Leitura(banco);
        Assert.Equal(10m, (await leitura.Clientes.FindAsync(cliente.Id))!.SaldoDevedor);
        var marcador = leitura.RegistrosAuditoria.Single(r => r.Acao == RegistroAuditoria.Acoes.FiadoLegadoNormalizado);
        Assert.Contains(cliente.Id.ToString(), marcador.Detalhes);
    }
}