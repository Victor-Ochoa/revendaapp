using Bunit;

using Mediator;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor;
using MudBlazor.Services;

using RevendaApp.Infrastructure;
using RevendaApp.Infrastructure.Tenancy;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Common;
using RevendaApp.Web.Features.Admin.Vendas;
using RevendaApp.Web.Features.ConfiguracaoEmpresa;
using RevendaApp.Web.Features.Usuarios;
using RevendaApp.Web.Features.Usuarios.Listar;

namespace RevendaApp.UnitTests.Components;

/// <summary>
/// A aba "Minha Empresa" é a <b>primeira</b> de <c>/configuracoes</c>: é onde o admin da empresa
/// ajusta o próprio cadastro, e o produto pediu que ela abra a tela.
/// </summary>
/// <remarks>
/// <b>Regressão que este teste trava:</b> a aba vivia por último com um comentário justificando a
/// posição. Um refactor que a movesse de volta passaria por toda a suíte sem este teste — a ordem
/// só estava garantida por leitura do código.
/// </remarks>
public class ConfiguracoesAbasTests
{
    [Fact]
    public async Task Minha_empresa_e_a_primeira_aba()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<AppDbContext>(InMemoryDbContextFactory.Create());
        ctx.Services.AddSingleton<ITenantContext>(
            new FakeTenantContext(TenantTeste.EmpresaId, nomeEmpresa: "Empresa Teste"));
        ctx.Services.AddSingleton<IdentidadeEmpresaService>();
        ctx.Services.AddSingleton(new VendasAtualizadasNotifier());
        ctx.Services.AddSingleton<ISender>(new SenderDeConfiguracoes());

        var cut = ctx.Render<Configuracoes>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindComponents<MudTabPanel>()));

        var abas = cut.FindComponents<MudTabPanel>();

        // A ordem de renderização dos painéis é a ordem declarada em `Configuracoes.razor`.
        Assert.Equal("Minha Empresa", abas[0].Instance.Text);
    }

    /// <summary>
    /// <see cref="ISender"/> de teste: a tela só dispara as duas consultas de carregamento. As
    /// listas voltam vazias — o objeto do teste é a ordem das abas, não o conteúdo.
    /// </summary>
    private sealed class SenderDeConfiguracoes : ISender
    {
        public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A tela usa as sobrecargas IQuery.");

        public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A tela não envia comandos ao carregar.");

        public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
        {
            switch (query)
            {
                case ListarUsuariosQuery:
                    return ValueTask.FromResult((TResponse)(object)Array.Empty<UsuarioListItem>());

                case ListarVendasQuery:
                    return ValueTask.FromResult((TResponse)(object)Array.Empty<VendaAdminListItem>());

                case ObterConfigEmpresaQuery:
                    return ValueTask.FromResult((TResponse)(object?)null!);

                default:
                    throw new NotSupportedException($"Consulta não esperada no teste: {query.GetType().Name}.");
            }
        }

        public ValueTask<object?> Send(object message, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A tela só usa o Send tipado.");

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