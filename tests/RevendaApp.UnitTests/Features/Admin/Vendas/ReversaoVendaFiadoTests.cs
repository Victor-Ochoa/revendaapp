using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Features.Admin.Vendas;

namespace RevendaApp.UnitTests.Features.Admin.Vendas;

/// <summary>
/// O estorno de uma venda fiado só pode devolver ao cliente o que ele ainda deve daquela venda
/// (<see cref="Venda.SaldoFiado"/>). Devolver o <see cref="Venda.ValorTotal"/> inteiro inventaria
/// crédito: o excedente já pago não é do cliente de volta, e o clamp em zero de
/// <see cref="Cliente.RegistrarPagamento"/> esconderia o erro quando o cliente não tivesse outra
/// dívida — por isso os cenários abaixo dão ao cliente uma segunda venda em aberto.
/// </summary>
public class ReversaoVendaFiadoTests
{
    [Fact]
    public async Task Reversao_de_venda_parcialmente_paga_abate_apenas_o_saldo_fiado()
    {
        await using var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria", "11988887777", "Rua A, 1");
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        var venda = new Venda(cliente.Id, FormaPagamento.Fiado);
        venda.AdicionarItem(Guid.NewGuid(), 1, 100m, 60m);
        db.Vendas.Add(venda);

        // Dívida de 150: 100 desta venda (40 já pagos) + 50 de outra venda em aberto.
        cliente.AdicionarSaldoDevedor(150m);
        venda.AbaterFiado(40m);
        cliente.RegistrarPagamento(40m);
        await db.SaveChangesAsync();

        await ReversaoVendaHelper.ReverterAsync(db, venda, CancellationToken.None);
        await db.SaveChangesAsync();

        // Estorna só o que ainda estava em aberto (60); os 40 pagos não voltam e a dívida da outra
        // venda (50) permanece intacta. O estorno antigo devolvia 100 e zerava essa dívida.
        Assert.Equal(50m, (await db.Clientes.FindAsync(cliente.Id))!.SaldoDevedor);
    }

    [Fact]
    public async Task Reversao_de_venda_quitada_nao_abate_divida_de_outra_venda()
    {
        await using var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria", "11988887777", "Rua A, 1");
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        var venda = new Venda(cliente.Id, FormaPagamento.Fiado);
        venda.AdicionarItem(Guid.NewGuid(), 1, 100m, 60m);
        db.Vendas.Add(venda);

        // Dívida restante de 50 é de OUTRA venda; esta já está quitada.
        cliente.AdicionarSaldoDevedor(150m);
        venda.AbaterFiado(100m);
        cliente.RegistrarPagamento(100m);
        await db.SaveChangesAsync();

        await ReversaoVendaHelper.ReverterAsync(db, venda, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(50m, (await db.Clientes.FindAsync(cliente.Id))!.SaldoDevedor);
    }

    [Fact]
    public async Task Reversao_de_venda_quitada_nao_lanca_com_saldo_zero()
    {
        await using var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria", "11988887777", "Rua A, 1");
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        var venda = new Venda(cliente.Id, FormaPagamento.Fiado);
        venda.AdicionarItem(Guid.NewGuid(), 1, 100m, 60m);
        db.Vendas.Add(venda);
        cliente.AdicionarSaldoDevedor(100m);
        venda.AbaterFiado(100m);
        cliente.RegistrarPagamento(100m);
        await db.SaveChangesAsync();

        // SaldoFiado == 0 → o helper não pode chamar RegistrarPagamento (lança com valor <= 0).
        await ReversaoVendaHelper.ReverterAsync(db, venda, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(0m, (await db.Clientes.FindAsync(cliente.Id))!.SaldoDevedor);
    }
}