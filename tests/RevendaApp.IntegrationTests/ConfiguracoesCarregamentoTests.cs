using System.Net;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Entities;
using RevendaApp.Infrastructure.Identity;
using RevendaApp.IntegrationTests.TestHelpers;
using RevendaApp.Web.Features.Auth;
using RevendaApp.Web.Features.Seed;

namespace RevendaApp.IntegrationTests.Tests;

/// <summary>
/// Regressão: abrir <c>/configuracoes</c> como admin da empresa derrubava a página com
/// <see cref="InvalidOperationException"/> ("A second operation was started on this context
/// instance") porque a aba ativa ("Minha Empresa") dispara a própria consulta no
/// <c>OnInitializedAsync</c> enquanto a página ainda aguarda as consultas dela — as duas no mesmo
/// <c>AppDbContext</c> scoped ao circuito.
/// </summary>
public class ConfiguracoesCarregamentoTests(WebAppFixture fixture)
    : IClassFixture<WebAppFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        var (userManager, roleManager, provider) = IdentityTestHelpers.CriarManagers(fixture.ConnectionString);
        try
        {
            await GarantirEmpresaDemoEAdminAsync(userManager, roleManager);
        }
        finally
        {
            provider.Dispose();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Abrir_configuracoes_como_admin_carrega_a_aba_minha_empresa_sem_erro_de_contexto()
    {
        using var client = fixture.CreateClient();

        var (token, handler) = await GetAntiforgeryFieldsAsync(client, "/login");
        using var login = await PostLoginAsync(client, token, handler, SeedDemoData.EmailAdminDemo, SeedDemoData.SenhaAdminDemo);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        using var response = await client.GetAsync("/configuracoes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("second operation", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidOperationException", html, StringComparison.Ordinal);

        // A aba "Minha Empresa" é a primeira: o nome da empresa carregado pela query dela tem de
        // aparecer no HTML.
        Assert.Contains(SeedDemoData.NomeEmpresaDemo, html, StringComparison.Ordinal);
    }

    private async Task GarantirEmpresaDemoEAdminAsync(
        UserManager<ApplicationUser> userManager, RoleManager<ApplicationRole> roleManager)
    {
        await SeedData.GarantirRolesAsync(roleManager);

        await using var db = fixture.CreateDbContext();

        var empresa = await db.Set<Empresa>().FirstOrDefaultAsync(e => e.Identificador == SeedDemoData.IdentificadorEmpresaDemo);
        if (empresa is null)
        {
            empresa = new Empresa(SeedDemoData.IdentificadorEmpresaDemo, SeedDemoData.NomeEmpresaDemo);
            db.Set<Empresa>().Add(empresa);
            await db.SaveChangesAsync();
        }

        var admin = await userManager.FindByEmailAsync(SeedDemoData.EmailAdminDemo);
        if (admin is null)
        {
            admin = new ApplicationUser
            {
                UserName = SeedDemoData.EmailAdminDemo,
                Email = SeedDemoData.EmailAdminDemo,
                EmailConfirmed = true,
                Nome = SeedDemoData.NomeAdminDemo,
                EmpresaId = empresa.Id,
                Ativo = true,
            };

            var criacao = await userManager.CreateAsync(admin, SeedDemoData.SenhaAdminDemo);
            Assert.True(criacao.Succeeded, string.Join("; ", criacao.Errors.Select(e => e.Description)));
        }
        else if (admin.EmpresaId != empresa.Id || !admin.Ativo)
        {
            admin.EmpresaId = empresa.Id;
            admin.Ativo = true;
            await userManager.UpdateAsync(admin);
        }

        if (!await userManager.IsInRoleAsync(admin, ApplicationRole.Admin))
        {
            await userManager.AddToRoleAsync(admin, ApplicationRole.Admin);
        }
    }

    private static async Task<(string Token, string Handler)> GetAntiforgeryFieldsAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]*)\"").Groups[1].Value;
        var handler = Regex.Match(html, "name=\"_handler\" value=\"([^\"]*)\"").Groups[1].Value;

        Assert.False(string.IsNullOrEmpty(token));
        Assert.False(string.IsNullOrEmpty(handler));

        return (token, handler);
    }

    private static Task<HttpResponseMessage> PostLoginAsync(
        HttpClient client, string token, string handler, string email, string password)
    {
        var form = new Dictionary<string, string>
        {
            ["_handler"] = handler,
            ["__RequestVerificationToken"] = token,
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["Input.RememberMe"] = "true",
        };

        return client.PostAsync("/login", new FormUrlEncodedContent(form));
    }
}