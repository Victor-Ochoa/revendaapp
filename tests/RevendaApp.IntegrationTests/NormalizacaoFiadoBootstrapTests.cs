using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.IntegrationTests.TestHelpers;
using RevendaApp.Web.Features.Fiado.Normalizacao;

namespace RevendaApp.IntegrationTests.Tests;

/// <summary>
/// Prova a separação central/tenant da normalização do fiado legado contra um Postgres real:
/// a leitura cross-tenant acontece no contexto central e a escrita acontece em contextos
/// tenant-bound, um por empresa. Os testes unitários não exercitam isso (lá a fábrica devolve
/// contextos na mesma base InMemory).
/// </summary>
public class NormalizacaoFiadoBootstrapTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Prepara o "primeiro boot" de forma determinística: reseta o banco e remove o marcador da
    /// normalização.
    /// </summary>
    /// <remarks>
    /// O marcador é global (<c>RegistrosAuditoria</c> sem filtro) e idempotente por natureza: com
    /// ele presente, <c>ExecutarAsync</c> retorna sem tocar nada. Ele chega aqui por dois caminhos
    /// fora do controle do teste — o <see cref="DatabaseFixture"/> sobe o <b>AppHost inteiro</b>
    /// (o processo <c>RevendaApp.Web</c> roda o próprio bootstrap, que já normaliza e grava o
    /// marcador), e uma execução anterior da classe também o deixa. Por isso a remoção é explícita
    /// e imediatamente antes da chamada, em vez de depender só do reset.
    /// </remarks>
    private async Task PrepararAsync()
    {
        await fixture.ResetDatabaseAsync();
        await LimparMarcadorAsync();
    }

    private async Task LimparMarcadorAsync()
    {
        using var central = fixture.CreateDbContextCentral();
        await central.Database.ExecuteSqlRawAsync(
            """DELETE FROM "RegistrosAuditoria" WHERE "Acao" = {0}""",
            RegistroAuditoria.Acoes.FiadoLegadoNormalizado);
    }

    /// <summary>
    /// Cria cliente + vendas fiado + pagamentos de abatimento (VendaId null) numa empresa,
    /// replicando o estado que o banco legado tinha: saldo do cliente reduzido pelos pagamentos,
    /// mas nenhuma venda sabendo quanto foi pago.
    /// </summary>
    private async Task<(Guid ClienteId, IReadOnlyList<Guid> VendaIds)> CriarCenarioAsync(
        Guid empresaId, decimal[] valoresFiado, decimal[] pagamentos)
    {
        await using var db = fixture.CreateDbContext(empresaId);

        var cliente = new Cliente("Maria", $"119{empresaId:N}"[..11], "Rua A, 1");
        db.Clientes.Add(cliente);

        // ItensVenda tem FK para Produtos: o item precisa de um produto real.
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 5);
        produto.RegistrarEntrada(100);
        db.Produtos.Add(produto);
        await db.SaveChangesAsync();

        var vendaIds = new List<Guid>(valoresFiado.Length);
        var dia = 1;
        foreach (var valor in valoresFiado)
        {
            var venda = new Venda(cliente.Id, FormaPagamento.Fiado, new DateTime(2026, 1, dia++, 10, 0, 0, DateTimeKind.Utc));
            venda.AdicionarItem(produto.Id, 1, valor, valor / 2);
            db.Vendas.Add(venda);
            vendaIds.Add(venda.Id);
            cliente.AdicionarSaldoDevedor(valor);
        }

        var diaPagamento = 1;
        foreach (var pagamento in pagamentos)
        {
            db.Pagamentos.Add(new Pagamento(cliente.Id, pagamento, FormaPagamento.Dinheiro,
                data: new DateTime(2026, 2, diaPagamento++, 10, 0, 0, DateTimeKind.Utc)));
            cliente.RegistrarPagamento(pagamento);
        }

        await db.SaveChangesAsync();
        return (cliente.Id, vendaIds);
    }

    [Fact]
    public async Task Normalizacao_corrige_banco_legado_contra_Postgres()
    {
        await PrepararAsync();
        var empresaA = TenantIntegracao.EmpresaId;
        var empresaB = (await CriarEmpresaAsync()).Id;

        // Empresa A: vendas de 60 e 40 com 70 pagos → a antiga quita, a nova recebe 10.
        var (_, vendasA) = await CriarCenarioAsync(empresaA, [60m, 40m], [70m]);
        // Empresa B: venda de 30 com 10 pagos → prova que a passada não parou na primeira empresa.
        var (_, vendasB) = await CriarCenarioAsync(empresaB, [30m], [10m]);

        using (var central = fixture.CreateDbContextCentral())
        {
            await NormalizarFiadoLegado.ExecutarAsync(
                central, id => fixture.CreateDbContext(id), CancellationToken.None);
        }

        // Asserts pelos Ids criados, não por índice: se outra classe deixar vendas na empresa
        // padrão (o volume do Postgres é persistente), a posição na lista mudaria.
        await using (var dbA = fixture.CreateDbContext(empresaA))
        {
            var antiga = await dbA.Vendas.Include(v => v.Itens).SingleAsync(v => v.Id == vendasA[0]);
            var nova = await dbA.Vendas.Include(v => v.Itens).SingleAsync(v => v.Id == vendasA[1]);
            Assert.Equal(VendaStatus.FiadoQuitado, antiga.Status);
            Assert.Equal(10m, nova.ValorPagoFiado);
            Assert.Equal(VendaStatus.FiadoAberto, nova.Status);
        }

        await using (var dbB = fixture.CreateDbContext(empresaB))
        {
            var venda = await dbB.Vendas.Include(v => v.Itens).SingleAsync(v => v.Id == vendasB[0]);
            Assert.Equal(10m, venda.ValorPagoFiado);
        }

        using var centralFinal = fixture.CreateDbContextCentral();
        Assert.True(await centralFinal.RegistrosAuditoria
            .AnyAsync(r => r.Acao == RegistroAuditoria.Acoes.FiadoLegadoNormalizado));
    }

    /// <summary>
    /// Guarda de regressão da separação central/tenant. O interceptor de <c>EmpresaId</c> só recusa
    /// entidades em estado <c>Added</c>; a normalização faz <c>UPDATE</c> em vendas já rastreadas,
    /// então passar um contexto central como fábrica <b>não lança</b> — ele apenas lê todas as
    /// empresas em cada passada e multiplica o trabalho. O contrato que realmente protege é a
    /// fábrica ser consultada uma vez por empresa, com o id da empresa: é isto que este teste pina.
    /// Se alguém "simplificar" a assinatura removendo a fábrica, este teste quebra.
    /// </summary>
    [Fact]
    public async Task Normalizacao_abre_um_contexto_por_empresa_com_o_id_correto()
    {
        await PrepararAsync();
        var empresaA = TenantIntegracao.EmpresaId;
        var empresaB = (await CriarEmpresaAsync()).Id;
        await CriarCenarioAsync(empresaA, [60m], [10m]);
        await CriarCenarioAsync(empresaB, [30m], [10m]);

        var empresasPedidas = new List<Guid>();

        using var central = fixture.CreateDbContextCentral();
        var resultado = await NormalizarFiadoLegado.ExecutarAsync(
            central,
            id =>
            {
                empresasPedidas.Add(id);
                return fixture.CreateDbContext(id);
            },
            CancellationToken.None);

        Assert.Equal([empresaA, empresaB], empresasPedidas.Order());
        // Cada empresa tem um cliente: o total não pode ser multiplicado por passadas.
        Assert.Equal(2, resultado.ClientesAnalisados);
    }

    private async Task<Empresa> CriarEmpresaAsync()
    {
        using var central = fixture.CreateDbContextCentral();
        return await EmpresaTestHelper.CriarEmpresaAsync(central, "Empresa Normalizacao");
    }
}