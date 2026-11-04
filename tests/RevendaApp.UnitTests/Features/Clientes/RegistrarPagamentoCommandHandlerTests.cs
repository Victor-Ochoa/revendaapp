using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Common;
using RevendaApp.Web.Features.Clientes.Pagar;

namespace RevendaApp.UnitTests.Features.Clientes;

public class RegistrarPagamentoCommandHandlerTests
{
    private static async Task<(RevendaApp.Infrastructure.AppDbContext Db, Cliente Cliente)> CriarClienteAsync(decimal saldoDevedor = 0m)
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria Souza", "11988887777", "Rua A, 1");
        if (saldoDevedor > 0)
        {
            cliente.AdicionarSaldoDevedor(saldoDevedor);
        }

        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();
        return (db, cliente);
    }

    private static async Task<Venda> RegistrarVendaFiadoAsync(
        RevendaApp.Infrastructure.AppDbContext db,
        Guid clienteId,
        decimal valorItem = 100m,
        DateTime? dataHora = null)
    {
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, valorItem, 60m, 80m);
        produto.RegistrarEntrada(20);
        db.Produtos.Add(produto);
        await db.SaveChangesAsync();

        var venda = new Venda(clienteId, FormaPagamento.Fiado, dataHora);
        venda.AdicionarItem(produto.Id, 1, valorItem, 60m);
        db.Vendas.Add(venda);
        await db.SaveChangesAsync();
        return venda;
    }

    [Fact]
    public async Task Handle_DeveCriarPagamentoEReduzirSaldoDevedor()
    {
        var (db, cliente) = await CriarClienteAsync(saldoDevedor: 100m);
        var notifier = new VendasAtualizadasNotifier();
        var handler = new RegistrarPagamentoCommandHandler(db, TenantTeste.Contexto(), notifier);

        var result = await handler.Handle(
            new RegistrarPagamentoCommand(cliente.Id, 40m, FormaPagamento.Dinheiro, "Pagamento parcial"),
            CancellationToken.None);

        Assert.Equal(60m, result.SaldoDevedorRestante);

        var clienteAtualizado = await db.Clientes.FindAsync(cliente.Id);
        Assert.Equal(60m, clienteAtualizado!.SaldoDevedor);

        var pagamento = Assert.Single(db.Pagamentos);
        Assert.Equal(cliente.Id, pagamento.ClienteId);
        Assert.Equal(40m, pagamento.Valor);
        Assert.Equal(FormaPagamento.Dinheiro, pagamento.FormaPagamento);
        Assert.Equal("Pagamento parcial", pagamento.Observacao);
    }

    [Fact]
    public async Task Handle_DeveMarcarVendaFiadoComoQuitada_QuandoPagamentoCobreValorTotal()
    {
        var (db, cliente) = await CriarClienteAsync(saldoDevedor: 100m);
        var venda = await RegistrarVendaFiadoAsync(db, cliente.Id, valorItem: 100m);
        var notifier = new VendasAtualizadasNotifier();
        var handler = new RegistrarPagamentoCommandHandler(db, TenantTeste.Contexto(), notifier);

        await handler.Handle(
            new RegistrarPagamentoCommand(cliente.Id, 100m, FormaPagamento.Pix),
            CancellationToken.None);

        var vendaAtualizada = await db.Vendas.FindAsync(venda.Id);
        Assert.Equal(VendaStatus.FiadoQuitado, vendaAtualizada!.Status);
    }

    [Fact]
    public async Task Handle_NaoDeveMarcarVendaQuitada_QuandoPagamentoForParcial()
    {
        var (db, cliente) = await CriarClienteAsync(saldoDevedor: 100m);
        var venda = await RegistrarVendaFiadoAsync(db, cliente.Id, valorItem: 100m);
        var notifier = new VendasAtualizadasNotifier();
        var handler = new RegistrarPagamentoCommandHandler(db, TenantTeste.Contexto(), notifier);

        await handler.Handle(
            new RegistrarPagamentoCommand(cliente.Id, 40m, FormaPagamento.Dinheiro),
            CancellationToken.None);

        var vendaAtualizada = await db.Vendas.FindAsync(venda.Id);
        Assert.Equal(VendaStatus.FiadoAberto, vendaAtualizada!.Status);
    }

    [Fact]
    public async Task Handle_DeveQuitarVendasMaisAntigasPrimeiro_Fifo()
    {
        var (db, cliente) = await CriarClienteAsync(saldoDevedor: 100m);
        var vendaAntiga = await RegistrarVendaFiadoAsync(db, cliente.Id, valorItem: 60m, dataHora: new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc));
        var vendaNova = await RegistrarVendaFiadoAsync(db, cliente.Id, valorItem: 40m, dataHora: new DateTime(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc));
        var notifier = new VendasAtualizadasNotifier();
        var handler = new RegistrarPagamentoCommandHandler(db, TenantTeste.Contexto(), notifier);

        // Paga 60 -> quita somente a venda mais antiga (FIFO).
        await handler.Handle(
            new RegistrarPagamentoCommand(cliente.Id, 60m, FormaPagamento.Dinheiro),
            CancellationToken.None);

        Assert.Equal(VendaStatus.FiadoQuitado, (await db.Vendas.FindAsync(vendaAntiga.Id))!.Status);
        Assert.Equal(VendaStatus.FiadoAberto, (await db.Vendas.FindAsync(vendaNova.Id))!.Status);
    }

    [Fact]
    public async Task Handle_NaoDeveQuitarVendaMaisNova_QuandoPagamentoNaoCobreAVendaMaisAntiga()
    {
        // Fiado de 60 (mais antiga) + 40 (mais nova). Um pagamento de 50 não fecha a mais antiga,
        // então NENHUMA venda pode ser marcada como quitada: o FIFO exige que a dívida mais antiga
        // seja coberta primeiro. Sem essa regra o pagamento "vazava" para a venda seguinte, que
        // ficava FiadoQuitado com a dívida antiga ainda em aberto — e o dashboard reconhece lucro
        // de toda venda FiadoQuitado.
        var (db, cliente) = await CriarClienteAsync(saldoDevedor: 100m);
        var vendaAntiga = await RegistrarVendaFiadoAsync(db, cliente.Id, valorItem: 60m, dataHora: new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc));
        var vendaNova = await RegistrarVendaFiadoAsync(db, cliente.Id, valorItem: 40m, dataHora: new DateTime(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc));
        var notifier = new VendasAtualizadasNotifier();
        var handler = new RegistrarPagamentoCommandHandler(db, TenantTeste.Contexto(), notifier);

        await handler.Handle(
            new RegistrarPagamentoCommand(cliente.Id, 50m, FormaPagamento.Dinheiro),
            CancellationToken.None);

        Assert.Equal(VendaStatus.FiadoAberto, (await db.Vendas.FindAsync(vendaAntiga.Id))!.Status);
        Assert.Equal(VendaStatus.FiadoAberto, (await db.Vendas.FindAsync(vendaNova.Id))!.Status);
    }

    [Fact]
    public async Task Handle_DeveQuitarVendaAntigaEIniciarAProxima_QuandoPagamentoCobreAmaisAntiga()
    {
        // 60 (antiga) + 40 (nova), pagamento de 70: fecha a antiga e sobram 10 para a nova, que
        // continua em aberto (parcial).
        var (db, cliente) = await CriarClienteAsync(saldoDevedor: 100m);
        var vendaAntiga = await RegistrarVendaFiadoAsync(db, cliente.Id, valorItem: 60m, dataHora: new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc));
        var vendaNova = await RegistrarVendaFiadoAsync(db, cliente.Id, valorItem: 40m, dataHora: new DateTime(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc));
        var notifier = new VendasAtualizadasNotifier();
        var handler = new RegistrarPagamentoCommandHandler(db, TenantTeste.Contexto(), notifier);

        await handler.Handle(
            new RegistrarPagamentoCommand(cliente.Id, 70m, FormaPagamento.Dinheiro),
            CancellationToken.None);

        Assert.Equal(VendaStatus.FiadoQuitado, (await db.Vendas.FindAsync(vendaAntiga.Id))!.Status);
        Assert.Equal(VendaStatus.FiadoAberto, (await db.Vendas.FindAsync(vendaNova.Id))!.Status);
        Assert.Equal(30m, (await db.Clientes.FindAsync(cliente.Id))!.SaldoDevedor);
    }

    [Fact]
    public async Task Handle_DeveNotificarVendasAtualizadas()
    {
        var (db, cliente) = await CriarClienteAsync(saldoDevedor: 100m);
        var notifier = new VendasAtualizadasNotifier();
        var notificado = false;
        using var sub = notifier.Subscribe(() => { notificado = true; return Task.CompletedTask; });
        var handler = new RegistrarPagamentoCommandHandler(db, TenantTeste.Contexto(), notifier);

        await handler.Handle(
            new RegistrarPagamentoCommand(cliente.Id, 100m, FormaPagamento.Pix),
            CancellationToken.None);

        Assert.True(notificado);
    }
}