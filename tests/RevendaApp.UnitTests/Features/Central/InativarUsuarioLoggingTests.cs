using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RevendaApp.Infrastructure;
using RevendaApp.Infrastructure.Identity;
using RevendaApp.Infrastructure.Tenancy;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Features.Central.UsuariosEmpresa;

namespace RevendaApp.UnitTests.Features.Central;

/// <summary>
/// A inativação de usuário já está commitada quando o <c>SecurityStamp</c> é renovado; se a
/// renovação falhar, o operador só vê uma mensagem genérica e a sessão viva do usuário continua
/// válida. Sem log de erro não há como o suporte descobrir <b>por que</b> a revogação falhou.
/// Estes testes exercitam o <c>Handle</c> real (InMemory + Identity) e exigem a trilha de log.
/// </summary>
public class InativarUsuarioLoggingTests
{
    [Fact]
    public async Task Handle_QuandoIdentityRecusaRenovarStamp_DeveRegistrarErroComUsuarioEEmpresa()
    {
        var empresaId = Guid.NewGuid();
        var logger = new LoggerColetor<InativarUsuarioEmpresaCommandHandler>();
        var (handler, command) = CriarCenario(empresaId, logger, lancarExcecaoNoStamp: false, falharStamp: true);

        var resultado = await handler.Handle(command, CancellationToken.None);

        Assert.False(resultado.Sucesso);

        var erro = Assert.Single(logger.Entradas, entrada => entrada.Nivel == LogLevel.Error);
        Assert.Equal(command.UsuarioId, erro.Propriedades["UsuarioId"]);
        Assert.Equal(command.EmpresaId, erro.Propriedades["EmpresaId"]);
        Assert.Contains("falha simulada", erro.Propriedades["Erros"]?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handle_QuandoRenovacaoDoStampLancaExcecao_DeveRegistrarErroComAExcecao()
    {
        var empresaId = Guid.NewGuid();
        var logger = new LoggerColetor<InativarUsuarioEmpresaCommandHandler>();
        var (handler, command) = CriarCenario(empresaId, logger, lancarExcecaoNoStamp: true, falharStamp: false);

        var resultado = await handler.Handle(command, CancellationToken.None);

        Assert.False(resultado.Sucesso);

        var erro = Assert.Single(logger.Entradas, entrada => entrada.Nivel == LogLevel.Error);
        Assert.IsType<InvalidOperationException>(erro.Excecao);
        Assert.Equal(command.UsuarioId, erro.Propriedades["UsuarioId"]);
        Assert.Equal(command.EmpresaId, erro.Propriedades["EmpresaId"]);
    }

    [Fact]
    public async Task Handle_QuandoRenovacaoDoStampFunciona_NaoDeveRegistrarErro()
    {
        var empresaId = Guid.NewGuid();
        var logger = new LoggerColetor<InativarUsuarioEmpresaCommandHandler>();
        var (handler, command) = CriarCenario(empresaId, logger, lancarExcecaoNoStamp: false, falharStamp: false);

        var resultado = await handler.Handle(command, CancellationToken.None);

        Assert.True(resultado.Sucesso);
        Assert.DoesNotContain(logger.Entradas, entrada => entrada.Nivel >= LogLevel.Error);
    }

    private static (InativarUsuarioEmpresaCommandHandler Handler, InativarUsuarioEmpresaCommand Command)
        CriarCenario(
            Guid empresaId,
            ILogger<InativarUsuarioEmpresaCommandHandler> logger,
            bool lancarExcecaoNoStamp,
            bool falharStamp)
    {
        // Super admin da central: o handler exige EhSuperAdmin e não usa EmpresaId do tenant.
        var tenant = new FakeTenantContext(ehSuperAdmin: true);
        var db = CriarContextoInMemory(tenant);

        var usuario = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "vendedor@empresa.com.br",
            Email = "vendedor@empresa.com.br",
            Nome = "Vendedor",
            EmpresaId = empresaId,
            Ativo = true,
        };

        db.Users.Add(usuario);
        db.SaveChanges();

        var provider = CriarIdentitySobre(db);
        var userManager = new UserManagerComFalhaNoStamp(provider, lancarExcecaoNoStamp, falharStamp);

        var handler = new InativarUsuarioEmpresaCommandHandler(
            db,
            tenant,
            userManager,
            authStateProvider: null,
            logger: logger);

        return (handler, new InativarUsuarioEmpresaCommand(empresaId, usuario.Id, Ativo: false));
    }

    /// <summary>
    /// Contexto InMemory com o aviso de transação ignorada suprimido: o handler abre transação
    /// explícita (<c>TransacaoComRetry</c>) e o provider InMemory não a suporta — sem a supressão
    /// o próprio EF lança <c>TransactionIgnoredWarning</c>. Não é o comportamento em produção
    /// (Postgres), é só o preço de exercitar o <c>Handle</c> real sem container.
    /// </summary>
    private static AppDbContext CriarContextoInMemory(ITenantContext tenant)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(avisos => avisos.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new SetEmpresaIdInterceptor(tenant))
            .Options;

        return new AppDbContext(options, tenant);
    }

    /// <summary>Identity real (UserManager/RoleManager) sobre o MESMO contexto InMemory do handler,
    /// para que <c>GetRolesAsync</c> e a checagem de último admin vejam os dados do teste.</summary>
    private static ServiceProvider CriarIdentitySobre(AppDbContext db)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(db);
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<AppDbContext>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// <see cref="UserManager{TUser}.UpdateSecurityStampAsync"/> é virtual de propósito para ser
    /// substituído: aqui ele simula as duas falhas possíveis (erros do Identity e exceção de
    /// infraestrutura) sem precisar de um store quebrado.
    /// </summary>
    private sealed class UserManagerComFalhaNoStamp : UserManager<ApplicationUser>
    {
        private readonly bool _lancarExcecao;
        private readonly bool _falhar;

        public UserManagerComFalhaNoStamp(
            IServiceProvider services,
            bool lancarExcecao,
            bool falhar)
            : base(
                services.GetRequiredService<IUserStore<ApplicationUser>>(),
                services.GetRequiredService<IOptions<IdentityOptions>>(),
                services.GetRequiredService<IPasswordHasher<ApplicationUser>>(),
                services.GetServices<IUserValidator<ApplicationUser>>(),
                services.GetServices<IPasswordValidator<ApplicationUser>>(),
                services.GetRequiredService<ILookupNormalizer>(),
                services.GetRequiredService<IdentityErrorDescriber>(),
                services,
                services.GetRequiredService<ILogger<UserManager<ApplicationUser>>>())
        {
            _lancarExcecao = lancarExcecao;
            _falhar = falhar;
        }

        public override Task<IdentityResult> UpdateSecurityStampAsync(ApplicationUser user)
        {
            if (_lancarExcecao)
            {
                return Task.FromException<IdentityResult>(
                    new InvalidOperationException("falha simulada ao gravar o SecurityStamp"));
            }

            return Task.FromResult(_falhar
                ? IdentityResult.Failed(new IdentityError { Code = "stamp", Description = "falha simulada" })
                : IdentityResult.Success);
        }
    }
}