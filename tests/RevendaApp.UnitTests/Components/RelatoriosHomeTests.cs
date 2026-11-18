using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor.Services;

using RevendaApp.Infrastructure;
using RevendaApp.Infrastructure.Tenancy;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Common;
using RevendaApp.Web.Features.Relatorios.Home;

namespace RevendaApp.UnitTests.Components;

public class RelatoriosHomeTests
{
    [Fact]
    public void RelatoriosHome_lista_os_quatro_relatorios_com_link()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        // IdentidadeEmpresaService só precisa do AppDbContext para a assinatura (o título não
        // toca o banco) — mesmo padrão de EstoqueListaAdminTests.
        ctx.Services.AddSingleton<AppDbContext>(InMemoryDbContextFactory.Create());
        ctx.Services.AddSingleton<ITenantContext>(new FakeTenantContext(TenantTeste.EmpresaId, nomeEmpresa: "Empresa Teste"));
        ctx.Services.AddSingleton<IdentidadeEmpresaService>();

        var cut = ctx.Render<RelatoriosHome>();

        Assert.Contains("/relatorios/estoque", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("/relatorios/vendas", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("/relatorios/fiado", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("/relatorios/vasilhames", cut.Markup, StringComparison.Ordinal);
    }
}