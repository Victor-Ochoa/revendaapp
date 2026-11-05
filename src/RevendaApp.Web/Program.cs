using System.Globalization;

using FluentValidation;

using Mediator;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;

using MudBlazor.Services;

using RevendaApp.Infrastructure;
using RevendaApp.Infrastructure.Identity;
using RevendaApp.Infrastructure.Tenancy;
using RevendaApp.Web.Common;
using RevendaApp.Web.Common.Authorization;
using RevendaApp.Web.Common.Behaviors;
using RevendaApp.Web.Common.Tenancy;
using RevendaApp.Web.Components;
using RevendaApp.Web.Features.Auth;
using RevendaApp.Web.Features.Auth.Login;
using RevendaApp.Web.Features.ConfiguracaoEmpresa;
using RevendaApp.Web.Features.Seed;
using RevendaApp.Web.Features.Usuarios.Cadastrar;

var builder = WebApplication.CreateBuilder(args);

// Default nacional do processo: textos gerados no servidor fora de request (auditoria de
// histórico, seed) formatam R$/dd-MM com pt-BR mesmo sem HttpContext ativo.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("pt-BR");

builder.AddServiceDefaults();

// Multi-empresa (multi-tenant): o AppDbContext precisa do ITenantContext da requisição/circuito
// corrente para aplicar o filtro global por empresa. O `AddNpgsqlDbContext` do Aspire registra o
// contexto com **pooling** (AddDbContextPool), e no pooling as opções/interceptores são montados
// UMA vez a partir do provider RAIZ — um ITenantContext scoped injetado no construtor ficaria
// "capturado" (vazio em Production; "Cannot resolve scoped service from root provider" em
// Development). Por isso registramos o contexto como **scoped** (AddDbContext com overload que
// recebe o IServiceProvider por escopo) e usamos `EnrichNpgsqlDbContext` para manter os recursos
// do Aspire (retry, health check, tracing, métricas) — o connection string "revendaapp" continua
// vindo da configuração do Aspire, como antes.
// O TenantContext prefere o principal da requisição HTTP (cobre o prerender SSR, onde o
// AuthenticationStateProvider ainda não está no escopo) e usa o provider do circuito como fallback.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddScoped<SetEmpresaIdInterceptor>();

builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("revendaapp"));
    options.AddInterceptors(serviceProvider.GetRequiredService<SetEmpresaIdInterceptor>());
});
builder.EnrichNpgsqlDbContext<AppDbContext>();

// Chaves de Data Protection no banco (e não no filesystem efêmero do container): sem isto cada
// restart gera chaves novas e invalida todos os cookies de autenticação e tokens antiforgery já
// emitidos (todo mundo é deslogado); com mais de uma réplica, cada instância teria chaves
// diferentes e a sessão quebraria ao trocar de instância. A tabela é criada pela migração
// DataProtectionKeys, aplicada no MigrateAsync() do bootstrap — as chaves só são lidas/gravadas no
// primeiro uso (emissão de cookie), que acontece depois disso.
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<AppDbContext>();

builder.Services
    .AddIdentity<ApplicationUser, ApplicationRole>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.User.RequireUniqueEmail = true;

        // Hardening de senha: o default do Identity é RequiredLength=6 (fraco). Subimos para 8
        // com variedade mínima de caracteres. Valida na criação de usuário (tela Configurações)
        // e no seed; senhas já hasheadas não são revalidadas, então não quebra usuários atuais.
        options.Password.RequiredLength = 8;
        options.Password.RequiredUniqueChars = 4;
        // Lockout por tentativas: default do Identity (5 falhas → 5 min bloqueado) já protege
        // o Login; deixamos explícito para documentar a intenção de segurança.
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

// Claims de multi-empresa (EmpresaId/EmpresaNome/EmpresaCor/EhSuperAdmin) no principal. O
// AddIdentity acima registra uma factory padrão; como o DI resolve o ÚLTIMO registro de
// IUserClaimsPrincipalFactory<ApplicationUser>, este AddScoped precisa vir DEPOIS dele para
// substituí-la — é o que faz o TenantContext (fail-closed) enxergar a empresa do usuário.
builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, ApplicationUserClaimsPrincipalFactory>();

// O SecurityStampValidator reemite o principal a cada ValidationInterval (padrão 30 min) a partir
// do usuário do NameIdentifier — que, numa impersonation, é o admin da empresa. Sem reaplicar as
// claims, o badge "Entrando como" e o "Voltar à central" se perdem na primeira renovação. O
// callback preserva as duas claims de impersonation; ver RenovacaoPrincipalImpersonacao.
builder.Services.Configure<SecurityStampValidatorOptions>(RenovacaoPrincipalImpersonacao.Configurar);

// AddIdentity já registra o cookie "Identity.Application" via AddIdentityCookies internamente;
// chamar AddAuthentication().AddIdentityCookies() de novo aqui provocaria
// "Scheme already exists: Identity.Application" no startup. Customizamos o cookie configurando
// o esquema já registrado.
builder.Services.ConfigureApplicationCookie(cookie =>
{
    // Redirect unauthenticated requests to our custom /login page instead of the
    // Identity UI default ("/Account/Login").
    cookie.LoginPath = "/login";
    // Redirect a usuário autenticado, porém sem a role exigida, para a página personalizada
    // de "Sem Autorização" (em vez do comportamento padrão de 403/redirect ao login).
    cookie.AccessDeniedPath = "/sem-autorizacao";

    // Em produção o TLS termina no Caddy e o app só vê HTTP. Como passamos a aceitar
    // X-Forwarded-Proto do proxy, o cookie precisa ser SEMPRE Secure fora de Development —
    // senão um X-Forwarded-Proto forjado conseguiria rebaixar o cookie de sessão para texto puro.
    // Em Development mantemos SameAsRequest para o http://localhost continuar funcionando.
    cookie.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;

    // Teto ABSOLUTO da sessão de impersonation ("Entrar como"): 8h contadas do início, validadas a
    // cada request pelo claim ImpersonacaoIniciadaEm. Sem isto o cookie sliding + o
    // SecurityStampValidator renovam o ticket para sempre e o "teto de 8h" prometido pelo
    // EntrarComoCommandHandler só valeria para sessão ociosa. O Configurar preserva o
    // OnValidatePrincipal do SecurityStampValidator (encadeia, não substitui) — ver
    // ValidadeAbsolutaImpersonacao.
    ValidadeAbsolutaImpersonacao.Configurar(cookie);

    // O middleware de autorização (políticas das rotas Blazor e Results.Forbid()) é avaliado
    // ANTES do componente, então o RoleAwareNotAuthorized não chega a rodar nesses casos. Aqui o
    // super admin da central (claim EhSuperAdmin, sem impersonation) que tenta uma página de
    // domínio (política ApenasEmpresa) é mandado para /central em vez de "Sem Autorização" —
    // mesmo critério do componente, sem nunca conceder acesso.
    cookie.Events.OnRedirectToAccessDenied = context =>
    {
        var usuario = context.HttpContext.User;
        var ehSuperAdmin = bool.TryParse(
            usuario.FindFirst(TenantContext.ClaimEhSuperAdmin)?.Value, out var valor) && valor;
        var impersonando = bool.TryParse(
            usuario.FindFirst(TenantContext.ClaimImpersonando)?.Value, out var impersonandoValor) && impersonandoValor;

        context.Response.Redirect(ehSuperAdmin && !impersonando ? "/central" : context.RedirectUri);
        return Task.CompletedTask;
    };
});

// Every page requires an authenticated user unless explicitly marked [AllowAnonymous]
// (e.g. the login page).
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    Politicas.AdicionarPoliticas(options);
});
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddScoped<IPasswordSignIn, IdentityPasswordSignIn>();
builder.Services.AddScoped<IUsuarioAutenticado, UsuarioAutenticado>();
builder.Services.AddScoped<IPrincipalDoUsuario, PrincipalDoUsuario>();
builder.Services.AddScoped<IUsuarioWriter, IdentityUsuarioWriter>();
builder.Services.AddScoped<VendasAtualizadasNotifier>();

// Identidade exibida na interface (nome/cor/logo da empresa; "Revenda.App" no modo central). Scoped
// porque memoiza o logo da empresa — evita reler o base64 (~500KB) a cada render do app bar.
builder.Services.AddScoped<IdentidadeEmpresaService>();
builder.Services.AddSingleton(TimeProvider.System);

// Globalização: app interno brasileiro → força pt-BR (R$, dd/MM/yyyy) para todos os requests,
// sem negociar com o navegador. Sem isso, em produção o SO do container (UTC/en) renderizaria
// "US$" e datas MM/dd.
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var ptBrasil = new CultureInfo("pt-BR");
    options.DefaultRequestCulture = new RequestCulture(ptBrasil, ptBrasil);
    options.SupportedCultures = [ptBrasil];
    options.SupportedUICultures = [ptBrasil];
    // Remove qualquer provedor baseado no navegador — sempre pt-BR.
    options.RequestCultureProviders.Clear();
});

builder.Services.AddMediator(options => options.ServiceLifetime = ServiceLifetime.Scoped);
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
// Ordem do pipeline (verificada empiricamente: o PRIMEIRO registrado é o MAIS EXTERNO). O
// ConcorrenciaBehavior fica por fora da validação para envolver o pipeline inteiro — um conflito
// de concorrência (que só ocorre no save do handler) é traduzido num único ponto, e a limpeza do
// change tracker vale para qualquer handler que mute Produto/Cliente. O registro vive na extensão
// para que o teste de DI pine a MESMA linha que a aplicação usa.
builder.Services.AddPipelineBehaviors();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Blazor Server em produção atravessa rede instável (WiFi/4G/LB) atrás de proxies:
// os defaults descartavam circuito ocioso (~3min) e o negotiate intermitente deixava a UI
// morta ("Circuit host not initialized"). Mantemos o circuito vivo por mais tempo e com
// heartbeat constante p/ a retenção não derrubar a interação (modais/menus) no próximo clique.
builder.Services.AddServerSideBlazor()
    .AddCircuitOptions(options =>
    {
        // Não descartar circuito por inatividade — abas ociosas (default ~3min) derrubavam
        // o circuito e a interação seguinte falhava o negotiate de novo.
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(30);
    })
    .AddHubOptions(hubOptions =>
    {
        // Heartbeat mais frequente mantém o circuito vivo mesmo em rede com jitter.
        hubOptions.KeepAliveInterval = TimeSpan.FromSeconds(15);
        hubOptions.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    });

builder.Services.AddMudServices();

var app = builder.Build();

app.MapDefaultEndpoints();

// Applies pending EF Core migrations automatically on startup, and seeds a default admin user
// so there is always a working login — simple approach suited to this app's scale.
// SeedDemoData (dados de demonstração) roda SOMENTE em ambientes não-Produção — em produção
// desligado via "Seed:EnableDemoData": false (appsettings.Production.json).
using (var scope = app.Services.CreateScope())
{
    // Sem trilha de log o bootstrap é uma caixa-preta: se a migração falhar ou o seed não rodar,
    // o operador não tem como saber em que passo o processo parou.
    var bootstrapLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    LogsDoBootstrap.AplicandoMigracoes(bootstrapLogger);

    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.MigrateAsync();

    var credentialsRequired = app.Environment.IsProduction();
    await SeedData.SeedDefaultAdminUserAsync(
        scope.ServiceProvider,
        app.Configuration,
        requireExplicitCredentials: credentialsRequired,
        logger: bootstrapLogger);

    if (app.Configuration.GetValue("Seed:EnableDemoData", !app.Environment.IsProduction()))
    {
        // No startup não existe circuito Blazor, então o ITenantContext da DI resolve "sem
        // empresa" (modo central) — semear com ele gravaria EmpresaId = Guid.Empty. O seed recebe
        // uma fábrica de contextos com o tenant FIXO da empresa demo: o filtro global e o
        // interceptor de EmpresaId passam a apontar para essa empresa.
        // As opções são montadas aqui (mesma connection string do Aspire) em vez de reaproveitar as
        // opções da DI: elas já carregam o interceptor do tenant central, que lançaria
        // EmpresaIdRequerida ao gravar domínio.
        AppDbContext CriarContextoDaEmpresa(Guid empresaId)
        {
            var tenant = new TenantFixo(empresaId);
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(app.Configuration.GetConnectionString("revendaapp"))
                .AddInterceptors(new SetEmpresaIdInterceptor(tenant))
                .Options;

            return new AppDbContext(options, tenant);
        }

        await SeedDemoData.SeedAsync(
            dbContext,
            CriarContextoDaEmpresa,
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>(),
            scope.ServiceProvider.GetRequiredService<TimeProvider>());
    }

    LogsDoBootstrap.Concluido(bootstrapLogger);
}

// Configure the HTTP request pipeline.
// Em produção o Caddy (host) termina o TLS e faz reverse_proxy para a porta publicada do
// container — o Kestrel enxerga o gateway do Docker, não loopback. Precisa vir ANTES do
// UseHttpsRedirection: o redirect só acerta o destino se o esquema já estiver corrigido. (O
// cookie de autenticação não depende disso: fora de Development o SecurePolicy é Always.)
var opcoesProxy = new ForwardedHeadersOptions();
ForwardedHeadersConfig.Configurar(opcoesProxy, app.Configuration[ForwardedHeadersConfig.ChaveRedesConfiaveis]);
app.UseForwardedHeaders(opcoesProxy);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// Aplica a cultura pt-BR para requests SSR e circuitos interativos do Blazor Server.
app.UseRequestLocalization();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAuthEndpoints();
app.MapConfiguracaoEmpresaEndpoints();

app.Run();

// Logs do bootstrap no padrão LoggerMessage (CA1848): a mensagem só é formatada quando o nível
// está habilitado. Ficam num tipo próprio porque métodos [LoggerMessage] precisam ser `partial`,
// o que não é possível em statements de topo.
internal static partial class LogsDoBootstrap
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Aplicando migrações do banco...")]
    public static partial void AplicandoMigracoes(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bootstrap concluído: migrações aplicadas e super admin verificado.")]
    public static partial void Concluido(ILogger logger);
}