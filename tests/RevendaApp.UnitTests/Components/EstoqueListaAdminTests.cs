using Bunit;

using Mediator;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor.Services;

using RevendaApp.Infrastructure;
using RevendaApp.Infrastructure.Tenancy;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Common;
using RevendaApp.Web.Common.Authorization;
using RevendaApp.Web.Features.Estoque.Listar;
using RevendaApp.Web.Features.Estoque.Vasilhames;

namespace RevendaApp.UnitTests.Components;

/// <summary>
/// A tela de estoque não esconde funcionalidades: os botões de mutação (Novo Produto, Editar,
/// Movimentar) ficam <b>visíveis</b> para o vendedor, apenas desabilitados com o tooltip "Apenas
/// administradores". O limite real é a checagem no handler; a UI informa em vez de enganar.
/// </summary>
/// <remarks>
/// <b>Regressão que estes testes travam:</b> se um refactor remover o <c>Disabled</c> do botão, o
/// vendedor volta a ver um botão que só falha no servidor; se o botão passar a ser ocultado, o
/// operador perde a descoberta da funcionalidade e não entende por que não pode usá-la.
/// </remarks>
public class EstoqueListaAdminTests
{
    /// <summary>
    /// Controle positivo: o administrador vê o botão "Novo Produto" habilitado — sem ele, um
    /// <c>Disabled</c> fixo (por exemplo <c>Disabled="true"</c>) passaria no teste do vendedor.
    /// </summary>
    [Fact]
    public async Task Administrador_ve_botao_novo_produto_habilitado()
    {
        await using var ctx = CriarContexto(ehAdmin: true);

        var cut = ctx.Render<EstoqueLista>();

        cut.WaitForAssertion(() => Assert.Contains("Novo Produto", cut.Markup, StringComparison.Ordinal));

        var botao = EncontrarBotao(cut, "Novo Produto");
        Assert.False(botao.HasAttribute("disabled"));
        Assert.Equal("/estoque/novo", botao.GetAttribute("href"));
    }

    /// <summary>
    /// O vendedor (não administrador) continua vendo o botão — a funcionalidade não é escondida —
    /// mas ele está desabilitado, porque a rota e o handler recusariam a operação.
    /// </summary>
    [Fact]
    public async Task Vendedor_ve_botao_novo_produto_desabilitado()
    {
        await using var ctx = CriarContexto(ehAdmin: false);

        var cut = ctx.Render<EstoqueLista>();

        cut.WaitForAssertion(() => Assert.Contains("Novo Produto", cut.Markup, StringComparison.Ordinal));

        var botao = EncontrarBotao(cut, "Novo Produto");
        Assert.True(botao.HasAttribute("disabled"));
    }

    /// <summary>
    /// O caminho do não-administrador não carrega o Controle de Vasilhames (mutação restrita ao
    /// admin). Trava que a tela não dispare a consulta de vasilhames para o vendedor.
    /// </summary>
    [Fact]
    public async Task Vendedor_nao_dispara_a_consulta_de_controle_de_vasilhames()
    {
        await using var ctx = CriarContexto(ehAdmin: false);
        var sender = ctx.Services.GetRequiredService<SenderDeEstoque>();

        var cut = ctx.Render<EstoqueLista>();

        cut.WaitForAssertion(() => Assert.Contains("Nenhum produto cadastrado ainda.", cut.Markup, StringComparison.Ordinal));

        Assert.Equal(1, sender.ConsultasDeProdutos);
        Assert.Equal(0, sender.ConsultasDeVasilhames);
    }

    /// <summary>
    /// Registra todas as dependências reais de <see cref="EstoqueLista"/> (<c>@inject</c>):
    /// <see cref="ISender"/>, <see cref="IDialogService"/>, <see cref="IUsuarioAutenticado"/>,
    /// <see cref="VendasAtualizadasNotifier"/> e <see cref="IdentidadeEmpresaService"/> — este
    /// último exige um <c>AppDbContext</c> só para a assinatura (o título não toca o banco).
    /// </summary>
    private static BunitContext CriarContexto(bool ehAdmin)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<AppDbContext>(InMemoryDbContextFactory.Create());
        ctx.Services.AddSingleton<ITenantContext>(new FakeTenantContext(TenantTeste.EmpresaId, nomeEmpresa: "Empresa Teste"));
        ctx.Services.AddSingleton<IdentidadeEmpresaService>();
        ctx.Services.AddSingleton(new VendasAtualizadasNotifier());
        ctx.Services.AddSingleton<IUsuarioAutenticado>(new FakeUsuarioAutenticado(ehAdmin));

        // A mesma instância responde como ISender e é resolvida no teste para contar as consultas.
        var sender = new SenderDeEstoque();
        ctx.Services.AddSingleton<ISender>(sender);
        ctx.Services.AddSingleton(sender);

        return ctx;
    }

    /// <summary>
    /// Localiza o controle pelo rótulo entre os <c>mud-button-root</c>. O seletor é de classe, não
    /// <c>button</c>, porque o MudBlazor renderiza um <c>&lt;a href&gt;</c> quando o botão está
    /// habilitado e um <c>&lt;button disabled&gt;</c> quando está desabilitado — o mesmo controle
    /// muda de tag conforme o estado, e a asserção precisa funcionar nos dois lados.
    /// </summary>
    private static AngleSharp.Dom.IElement EncontrarBotao(IRenderedComponent<EstoqueLista> cut, string texto) =>
        cut.FindAll(".mud-button-root").First(b => b.TextContent.Contains(texto, StringComparison.Ordinal));

    /// <summary>
    /// <see cref="ISender"/> de teste: a tela de estoque só consulta produtos e o controle de
    /// vasilhames; nenhuma mutação é exercitada aqui.
    /// </summary>
    private sealed class SenderDeEstoque : ISender
    {
        public int ConsultasDeProdutos { get; private set; }

        public int ConsultasDeVasilhames { get; private set; }

        public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A tela de estoque usa as sobrecargas IQuery.");

        public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A tela de estoque não envia comandos ao carregar.");

        public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
        {
            switch (query)
            {
                case ListarProdutosQuery:
                    ConsultasDeProdutos++;
                    return ValueTask.FromResult((TResponse)(object)Array.Empty<ProdutoListItem>());

                case ObterControleVasilhamesQuery:
                    ConsultasDeVasilhames++;
                    return ValueTask.FromResult((TResponse)(object)Array.Empty<ControleVasilhameItem>());

                default:
                    throw new NotSupportedException($"Consulta não esperada no teste: {query.GetType().Name}.");
            }
        }

        public ValueTask<object?> Send(object message, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A tela de estoque só usa o Send tipado.");

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