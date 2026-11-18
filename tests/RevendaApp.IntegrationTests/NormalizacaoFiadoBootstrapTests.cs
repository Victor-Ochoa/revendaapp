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
    /// Cria cliente + vendas fiado + pagamentos de abatimento (VendaId null) numa empresa,
    /// replicando o estado que o banco legado tinha: saldo do cliente reduzido pelos pagamentos,
    /// mas nenhuma venda sabendo quanto foi pago.
    /// </summary>
    private async Task<Guid> CriarCenarioAsync(Guid empresaId, decimal[] valoresFiado, decimal[] pagamentos)
    {
        await using var db = fixture.CreateDbContext(empresaId);

        var cliente = new Cliente("Maria", $"119{empresaId:N}"[..11], "Rua A, 1");
        db.Clientes.Add(cliente);

        // ItensVenda tem FK para Produtos: o item precisa de um produto real.
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 5);
        produto.RegistrarEntrada(100);
        db.Produtos.Add(produto);
        await db.SaveChangesAsync();

        var dia = 1;
        foreach (var valor in valoresFiado)
        {
            var venda = new Venda(cliente.Id, FormaPagamento.Fiado, new DateTime(2026, 1, dia++, 10, 0, 0, DateTimeKind.Utc));
            venda.AdicionarItem(produto.Id, 1, valor, valor / 2);
            db.Vendas.Add(venda);
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
        return cliente.Id;
    }

    [Fact]
    public async Task Normalizacao_corrige_banco_legado_contra_Postgres()
    {
        var empresaA = TenantIntegracao.EmpresaId;
        var empresaB = (await CriarEmpresaAsync()).Id;

        // Empresa A: vendas de 60 e 40 com 70 pagos → a antiga quita, a nova recebe 10.
        await CriarCenarioAsync(empresaA, [60m, 40m], [70m]);
        // Empresa B: venda de 30 com 10 pagos → prova que a passada não parou na primeira empresa.
        await CriarCenarioAsync(empresaB, [30m], [10m]);

        using (var central = fixture.CreateDbContextCentral())
        {
            await NormalizarFiadoLegado.ExecutarAsync(
                central, id => fixture.CreateDbContext(id), CancellationToken.None);
        }

        await using (var dbA = fixture.CreateDbContext(empresaA))
        {
            var vendas = await dbA.Vendas.Include(v => v.Itens).OrderBy(v => v.DataHora).ToListAsync();
            Assert.Equal(VendaStatus.FiadoQuitado, vendas[0].Status);
            Assert.Equal(10m, vendas[1].ValorPagoFiado);
            Assert.Equal(VendaStatus.FiadoAberto, vendas[1].Status);
        }

        await using (var dbB = fixture.CreateDbContext(empresaB))
        {
            var venda = await dbB.Vendas.Include(v => v.Itens).SingleAsync();
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