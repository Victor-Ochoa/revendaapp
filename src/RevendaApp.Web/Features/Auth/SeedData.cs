using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

using RevendaApp.Infrastructure.Identity;

namespace RevendaApp.Web.Features.Auth;

/// <summary>
/// Garante que sempre exista um <b>super administrador da central</b> para o primeiro acesso ao
/// sistema. As credenciais podem ser sobrescritas via configuração (chaves "SeedAdmin:Email" e
/// "SeedAdmin:Password"); caso contrário, usa um valor padrão adequado apenas para desenvolvimento.
/// </summary>
/// <remarks>
/// <para>
/// Multi-empresa: o bootstrap cria apenas o super admin <b>sem empresa</b> (<c>EmpresaId = null</c>)
/// na role <see cref="ApplicationRole.SuperAdmin"/>. Empresas e seus administradores são criados
/// depois, pela central — o bootstrap nunca cria empresa nem admin de empresa.
/// </para>
/// </remarks>
public static partial class SeedData
{
    public const string DefaultEmail = "admin@revenda.app.com.br";
    public const string DefaultPassword = "RevendaApp@123";

    /// <summary>Nome de exibição do super admin criado no bootstrap.</summary>
    public const string NomeSuperAdmin = "Super Administrador";

    /// <param name="services">Provider com o escopo do bootstrap (UserManager/RoleManager).</param>
    /// <param name="configuration">Configuração com "SeedAdmin:Email"/"SeedAdmin:Password".</param>
    /// <param name="requireExplicitCredentials">
    /// Em Produção, exige as credenciais configuradas em vez de cair no default de desenvolvimento.
    /// </param>
    /// <param name="logger">
    /// Logger opcional do bootstrap. Opcional porque os testes chamam o seed direto, sem host; sem
    /// ele o seed continua funcionando, apenas sem trilha de log.
    /// </param>
    public static async Task SeedDefaultAdminUserAsync(IServiceProvider services, IConfiguration configuration,
        bool requireExplicitCredentials = false, ILogger? logger = null)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();

        var email = configuration["SeedAdmin:Email"];
        var password = configuration["SeedAdmin:Password"];

        if (requireExplicitCredentials)
        {
            // Em Produção não há fallback para o default de dev: o super admin DEVE ter sido
            // configurado via ambiente (SeedAdmin:Email / SeedAdmin:Password). Falha rápido em vez
            // de subir com credenciais conhecidas e fracas.
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException(
                    "Credenciais do admin não configuradas em Produção. Defina SeedAdmin:Email e SeedAdmin:Password via variáveis de ambiente.");
            }
        }
        else
        {
            email ??= DefaultEmail;
            password ??= DefaultPassword;
        }

        // Garante que as roles existam (criando-as se não existirem) ANTES de qualquer verificação.
        // Sem isso, num banco onde o usuário já foi criado por execução anterior (comum em
        // produção), o seed retornava cedo e as roles nunca eram criadas.
        await GarantirRolesAsync(roleManager);

        var usuario = await userManager.FindByEmailAsync(email);
        if (usuario is null)
        {
            usuario = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                Nome = NomeSuperAdmin,
                // Super admin da central: sem empresa e ativo. Explícito (não depende do default
                // do banco/propriedade) para não criar usuário inativo por engano.
                EmpresaId = null,
                Ativo = true,
            };

            var result = await userManager.CreateAsync(usuario, password);
            if (!result.Succeeded)
            {
                var errors = string.Join("; ", result.Errors.Select(e => e.Description));
                throw new InvalidOperationException($"Falha ao criar o usuário super administrador padrão: {errors}");
            }

            // Só o e-mail: a senha NUNCA vai para o log.
            if (logger is not null)
            {
                LogSuperAdminCriado(logger, email);
            }
        }
        else
        {
            // Upgrade de banco legado: o usuário do seed TEM que ser o super admin da central.
            // Só adicionar a role SuperAdmin não basta — um usuário com EmpresaId continuaria
            // sendo um super admin com escopo de empresa, e um usuário com a role Admin acumulada
            // passaria por Politicas.ApenasAdmin/RequerAdminAsync enquanto o TenantContext está em
            // modo central (EmpresaId nulo), lendo e gravando dados de todas as empresas.
            if (usuario.EmpresaId is not null)
            {
                throw new InvalidOperationException(
                    $"O usuário do seed ({email}) é o super admin da central e não pode pertencer a uma empresa (EmpresaId definido). Corrija o usuário antes de iniciar.");
            }

            // Corrige o nome de exibição deixado pelo seed antigo ("Administrador").
            if (usuario.Nome != NomeSuperAdmin)
            {
                usuario.Nome = NomeSuperAdmin;
                var atualizacao = await userManager.UpdateAsync(usuario);
                if (!atualizacao.Succeeded)
                {
                    var errors = string.Join("; ", atualizacao.Errors.Select(e => e.Description));
                    throw new InvalidOperationException(
                        $"Falha ao atualizar o nome do super administrador da central: {errors}");
                }
            }
        }

        // Garante o vínculo do super admin à role SuperAdmin mesmo quando o usuário já existia
        // (cenário de banco antigo em que o seed parava antes de AddToRoleAsync). Idempotente.
        if (!await userManager.IsInRoleAsync(usuario, ApplicationRole.SuperAdmin))
        {
            await userManager.AddToRoleAsync(usuario, ApplicationRole.SuperAdmin);
        }

        // Remove a role Admin legada DEPOIS de garantir SuperAdmin (nunca deixa o usuário sem
        // role): o super admin da central é SuperAdmin APENAS, senão passaria por
        // Politicas.ApenasAdmin/RequerAdminAsync com o TenantContext em modo central.
        if (await userManager.IsInRoleAsync(usuario, ApplicationRole.Admin))
        {
            var remocao = await userManager.RemoveFromRoleAsync(usuario, ApplicationRole.Admin);
            if (!remocao.Succeeded)
            {
                var errors = string.Join("; ", remocao.Errors.Select(e => e.Description));
                throw new InvalidOperationException(
                    $"Falha ao remover a role Admin do super administrador da central: {errors}");
            }
        }
    }

    /// <summary>Trilha do bootstrap: registra apenas o e-mail do super admin criado (nunca a senha).</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "Super admin da central criado: {Email}")]
    private static partial void LogSuperAdminCriado(ILogger logger, string email);

    /// <summary>Cria as roles <see cref="ApplicationRole.Admin"/>, <see cref="ApplicationRole.Vendedor"/>
    /// e <see cref="ApplicationRole.SuperAdmin"/> se ainda não existirem. Idempotente — chamado no
    /// seed e também quando um admin cadastra usuários.</summary>
    public static async Task GarantirRolesAsync(RoleManager<ApplicationRole> roleManager)
    {
        foreach (var roleName in new[] { ApplicationRole.Admin, ApplicationRole.Vendedor, ApplicationRole.SuperAdmin })
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                await roleManager.CreateAsync(new ApplicationRole(roleName));
            }
        }
    }
}