using Bunit;

using Mediator;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

using MudBlazor;
using MudBlazor.Services;

using RevendaApp.Domain.Enums;
using RevendaApp.Web.Common;
using RevendaApp.Web.Features.Estoque.Listar;
using RevendaApp.Web.Features.Vendas.NovaVenda;

namespace RevendaApp.UnitTests.Components;

/// <summary>
/// Uma falha de negócio na venda (estoque insuficiente, cliente inexistente) precisa virar
/// <b>mensagem na modal</b> — não exceção no circuito. No Blazor Server, exceção não tratada
/// derruba a conexão e o operador perde a venda inteira que estava montando.
/// </summary>
/// <remarks>
/// O comportamento já existe em <c>ConfirmarAsync</c> (o <c>catch (InvalidOperationException)</c>
/// grava em <c>_errorMessage</c>, renderizado num <c>MudAlert Severity=Error</c>). O teste existe
/// para <b>travá-lo</b>: um refactor que remova o catch ou o alerta derruba este teste, e não o
/// operador.
/// </remarks>
public class NovaVendaDialogTests
{
    private const string MensagemDeNegocio = "Estoque insuficiente para o produto Gás 13kg.";

    /// <summary>
    /// Caminho crítico: o handler recusa a venda com <see cref="InvalidOperationException"/> (regra
    /// de negócio), a mensagem aparece na tela e o diálogo <b>continua aberto</b> — o operador
    /// corrige a quantidade e tenta de novo, sem remontar a venda do zero.
    /// </summary>
    [Fact]
    public async Task Falha_de_negocio_mostra_mensagem_em_vez_de_derrubar_o_circuito()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var sender = new SenderQueRecusaVenda();
        ctx.Services.AddSingleton<ISender>(sender);
        ctx.Services.AddSingleton(new VendasAtualizadasNotifier());

        var (_, cut) = RenderDialogo(ctx);

        cut.Find("button.mud-button-filled").Click();

        cut.WaitForAssertion(() => Assert.Contains(MensagemDeNegocio, cut.Markup, StringComparison.Ordinal));
        Assert.Contains("mud-alert-text-error", cut.Markup, StringComparison.Ordinal);

        // O diálogo não foi fechado: continua renderizado com o botão de confirmar disponível, e a
        // tentativa chegou de fato ao handler (a falha veio da regra de negócio, não do clique).
        Assert.Equal(1, sender.VendasTentadas);
        Assert.Contains("Registrar Venda", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Controle positivo do teste acima: com o mesmo cenário de cliente/produto e um handler que
    /// aceita, nenhuma mensagem de erro aparece — a asserção do teste anterior não passa por
    /// vacuidade (o texto só existe quando há falha).
    /// </summary>
    [Fact]
    public async Task Venda_aceita_nao_mostra_mensagem_de_erro()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<ISender>(new SenderQueRecusaVenda(falhar: false));
        ctx.Services.AddSingleton(new VendasAtualizadasNotifier());

        var (provider, dialogo) = RenderDialogo(ctx);

        dialogo.Find("button.mud-button-filled").Click();

        // Sucesso fecha o diálogo: o componente sai da árvore e o provider deixa de renderizar a
        // modal. A ausência da mensagem de erro é verificada no markup do provider (que sobrevive),
        // sem tocar no componente já descartado.
        provider.WaitForAssertion(() => Assert.DoesNotContain("Registrar Venda", provider.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain(MensagemDeNegocio, provider.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Renderiza o diálogo dentro de um <c>MudDialogProvider</c> real (é ele que fornece o
    /// <see cref="IMudDialogInstance"/> por cascata). O cliente inicial é pré-selecionado para que
    /// "Registrar Venda" fique habilitado — sem cliente a modal abre com a configuração bloqueada.
    /// </summary>
    private static (IRenderedComponent<DialogHost> Provider, IRenderedComponent<NovaVendaDialog> Dialogo) RenderDialogo(BunitContext ctx)
    {
        var provider = ctx.Render<DialogHost>();
        var dialogService = ctx.Services.GetRequiredService<IDialogService>();

        var parametros = new DialogParameters<NovaVendaDialog>
        {
            { x => x.ClienteInicialId, SenderQueRecusaVenda.ClienteId },
            { x => x.ProdutoInicialId, SenderQueRecusaVenda.ProdutoId },
        };

        _ = provider.InvokeAsync(() => dialogService.ShowAsync<NovaVendaDialog>("Nova venda", parametros));

        provider.WaitForState(() => provider.Markup.Contains("Registrar Venda", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        return (provider, provider.FindComponent<NovaVendaDialog>());
    }

    /// <summary>Hospeda o <c>MudDialogProvider</c> (fornece o <see cref="IMudDialogInstance"/>).</summary>
    private sealed class DialogHost : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
        }
    }

    /// <summary>
    /// <see cref="ISender"/> de teste: responde às consultas de carregamento com um cliente e um
    /// produto válidos (a modal precisa deles para liberar a configuração) e, na gravação, ou lança
    /// a recusa de negócio, ou devolve uma venda registrada.
    /// </summary>
    private sealed class SenderQueRecusaVenda(bool falhar = true) : ISender
    {
        public static readonly Guid ClienteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        public static readonly Guid ProdutoId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        /// <summary>Quantas vezes o handler de registro de venda foi acionado (prova que o clique enviou).</summary>
        public int VendasTentadas { get; private set; }

        public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A modal usa as sobrecargas IQuery e ICommand.");

        public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
        {
            if (command is RegistrarVendaCommand)
            {
                VendasTentadas++;

                return falhar
                    ? throw new InvalidOperationException(MensagemDeNegocio)
                    : ValueTask.FromResult((TResponse)(object)new RegistrarVendaResult(Guid.NewGuid(), ClienteId, 100m, 20m));
            }

            throw new NotSupportedException($"Comando não esperado no teste: {command.GetType().Name}.");
        }

        public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
        {
            switch (query)
            {
                case ListarProdutosQuery:
                    return ValueTask.FromResult((TResponse)(object)new List<ProdutoListItem>
                    {
                        new(ProdutoId, "Gás 13kg", TipoProduto.Gas, 100m, 80m, 90m, 5, 1, false, true, false, 0, 0, 0),
                    });

                case ObterClienteParaVendaQuery:
                    return ValueTask.FromResult((TResponse)(object)new ClienteSugestao(
                        ClienteId, "Cliente Teste", "(11) 90000-0000", "Rua Teste, 100", 0m, 0m));

                case ObterPrecosPersonalizadosQuery:
                    return ValueTask.FromResult((TResponse)(object)new Dictionary<Guid, decimal>());

                default:
                    throw new NotSupportedException($"Consulta não esperada no teste: {query.GetType().Name}.");
            }
        }

        public ValueTask<object?> Send(object message, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A modal só usa o Send tipado.");

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamQuery<TResponse> query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamCommand<TResponse> command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object message, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}