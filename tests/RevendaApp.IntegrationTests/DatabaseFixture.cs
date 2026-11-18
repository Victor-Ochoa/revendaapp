using Aspire.Hosting;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using RevendaApp.Infrastructure;
using RevendaApp.Infrastructure.Tenancy;
using RevendaApp.IntegrationTests.TestHelpers;

namespace RevendaApp.IntegrationTests.Tests;

/// <summary>
/// Sobe apenas o Postgres real (via o mesmo AppHost usado em produção) para testar handlers que
/// usam <see cref="AppDbContext"/> diretamente, sem o overhead de subir o processo web inteiro.
/// </summary>
/// <remarks>
/// <para>
/// Multi-empresa: o contexto padrão desta fixture é <b>filtrado pela empresa padrão dos testes</b>
/// (<see cref="EmpresaIdPadrao"/>, o mesmo <c>EmpresaId</c> de
/// <see cref="TenantIntegracao.EmpresaId"/>). Assim os testes existentes exercitam o filtro global
/// sem precisar passar o tenant em cada <c>CreateDbContext()</c>.
/// </para>
/// <para>
/// Para ver todas as empresas (preparação/inspeção cross-tenant) use
/// <see cref="CreateDbContextCentral"/>; para agir como outra empresa, use
/// <see cref="CreateDbContext(Guid?)"/> com o id dela.
/// </para>
/// </remarks>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private const string IdentificadorEmpresaPadrao = "empresa-padrao-testes";

    /// <summary>
    /// Tabelas esvaziadas por <see cref="ResetDatabaseAsync"/>. A ordem é irrelevante para o
    /// <c>TRUNCATE</c>; está listada de filha para pai só por legibilidade.
    /// </summary>
    /// <remarks>
    /// <b>Fonte única da lista.</b> <see cref="ResetDatabaseAsync"/> monta o <c>TRUNCATE</c> a partir
    /// dela e o teste <c>ResetDatabaseTests.Nenhuma_tabela_fora_do_reset_referencia_uma_tabela_do_reset</c>
    /// valida que o conjunto é fechado sob FKs — assim remover uma tabela daqui deixa o teste
    /// vermelho, em vez de o <c>CASCADE</c> mascarar a omissão.
    /// </remarks>
    public static IReadOnlyList<string> TabelasDoReset { get; } =
    [
        "ItensVenda",
        "Pagamentos",
        "Vendas",
        "EmprestimosVasilhames",
        "MovimentacoesEstoque",
        "HistoricosEstoque",
        "PrecosPersonalizadosClientes",
        "RecorrenciasCompra",
        "Produtos",
        "Clientes",
        "AspNetUserRoles",
        "AspNetUserClaims",
        "AspNetUserLogins",
        "AspNetUserTokens",
        "AspNetRoleClaims",
        "AspNetUsers",
        "AspNetRoles",
        "RegistrosAuditoria",
        "Empresas",
    ];

    private readonly Guid _empresaIdPadrao = TenantIntegracao.EmpresaId;

    private DistributedApplication _app = null!;
    private string _connectionString = null!;

    /// <summary>Connection string do Postgres real gerenciado pelo Aspire.</summary>
    public string ConnectionString => _connectionString;

    /// <summary>
    /// Empresa padrão dos testes de integração — a mesma referenciada por
    /// <see cref="TenantIntegracao.EmpresaId"/>. Garantida (linha em <c>Empresas</c>) por
    /// <see cref="ResetDatabaseAsync"/>.
    /// </summary>
    public Guid EmpresaIdPadrao => _empresaIdPadrao;

    public async Task InitializeAsync()
    {
        var cancellationToken = CancellationToken.None;

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.RevendaApp_AppHost>(cancellationToken);

        // O AppHost registra seed-admin-email/seed-admin-password como Parameters sem default
        // (valor vem de config em produção). Sob o Aspire.Hosting.Testing, prover valores evita
        // MissingParameterValueException no build/start do AppHost.
        appHost.Configuration["Parameters:seed-admin-email"] = "admin@revenda.app.com.br";
        appHost.Configuration["Parameters:seed-admin-password"] = "RevendaApp@123";

        appHost.Services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));

        _app = await appHost.BuildAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(150), cancellationToken);
        await _app.StartAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(150), cancellationToken);
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("revendaapp", cancellationToken).WaitAsync(TimeSpan.FromSeconds(150), cancellationToken);

        // Espera o bootstrap do `web` terminar. `StartAsync` sobe TODOS os recursos do AppHost,
        // inclusive o processo da aplicação, que escreve no MESMO banco (migrações, normalização do
        // fiado legado — grava um marcador em `RegistrosAuditoria` — e seed de demonstração).
        // O `WaitForResourceHealthyAsync("web")` NÃO serve aqui: em Production o `/health` nem é
        // mapeado, e o Aspire considera o projeto saudável assim que o processo sobe — o bootstrap
        // segue rodando. Sem esta espera, o `ResetDatabaseAsync()` do primeiro teste trunca as
        // tabelas enquanto o bootstrap ainda grava: `TRUNCATE` e `INSERT` disputam locks (deadlock)
        // e o marcador gravado depois do reset vira registro fantasma nos testes de auditoria.
        // O Kestrel só começa a escutar DEPOIS do bloco de bootstrap, então responder a qualquer
        // requisição é o sinal de prontidão real.
        await AguardarWebProntaAsync(cancellationToken);

        _connectionString = await _app.GetConnectionStringAsync("revendaapp", cancellationToken)
            ?? throw new InvalidOperationException("Não foi possível resolver a connection string do recurso 'revendaapp'.");
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
    }

    /// <summary>
    /// Espera o processo <c>web</c> terminar o bootstrap (migração, normalização do fiado legado,
    /// seed) e começar a aceitar requisições.
    /// </summary>
    /// <remarks>
    /// O health check do Aspire para um projeto só atesta que o processo subiu — o bootstrap roda
    /// depois e escreve no mesmo banco que os testes limpam. O Kestrel abre a porta no fim do
    /// bootstrap, então uma resposta HTTP (qualquer status) prova que as escritas de bootstrap já
    /// aconteceram. O caminho <c>/</c> é usado de propósito: qualquer código de resposta serve, e
    /// não dependemos de <c>/health</c>, que só existe em Development.
    /// </remarks>
    private async Task AguardarWebProntaAsync(CancellationToken cancellationToken)
    {
        var endereco = _app.GetEndpoint("web", "http");
        using var http = new HttpClient { BaseAddress = endereco, Timeout = TimeSpan.FromSeconds(10) };

        var limite = DateTime.UtcNow.AddSeconds(150);
        while (true)
        {
            try
            {
                using var resposta = await http.GetAsync("/", cancellationToken);
                return;
            }
            catch (HttpRequestException) when (DateTime.UtcNow < limite)
            {
                // Ainda não escutando: bootstrap em andamento.
            }
            catch (TaskCanceledException) when (DateTime.UtcNow < limite)
            {
                // Timeout da requisição com a porta aberta mas sem resposta: tenta de novo.
            }

            if (DateTime.UtcNow >= limite)
            {
                throw new InvalidOperationException(
                    $"O recurso 'web' não começou a responder em {endereco} dentro do prazo de 150s.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    /// <summary>
    /// Cria um <see cref="AppDbContext"/> filtrado pela <b>empresa padrão dos testes</b>
    /// (<see cref="EmpresaIdPadrao"/>), garantindo que as migrações estejam aplicadas. É o modo
    /// normal de teste: as linhas gravadas por este contexto são tagueadas com a empresa padrão
    /// pelo <see cref="SetEmpresaIdInterceptor"/> e as leituras só enxergam essa empresa.
    /// </summary>
    public AppDbContext CreateDbContext() => CreateDbContext(EmpresaIdPadrao);

    /// <summary>
    /// Cria um <see cref="AppDbContext"/> de uma empresa específica. <c>null</c> equivale ao modo
    /// central (vê todas as empresas) — prefira <see cref="CreateDbContextCentral"/> por clareza.
    /// </summary>
    public AppDbContext CreateDbContext(Guid? empresaId)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString);

        // O interceptor é o único lugar que preenche `EmpresaId` na escrita (as entidades têm
        // setter privado). Ele é instalado SEMPRE — inclusive no modo central (`empresaId` null):
        // nesse caso `EmpresaIdRequerida` lança em qualquer `Added` de entidade de domínio, o que é
        // a guarda desejada. Sem o interceptor, uma escrita central gravaria `EmpresaId = Guid.Empty`
        // (não há FK para `Empresas`) e a linha ficaria invisível para todos os tenants — fazendo
        // qualquer assert de ausência passar por vacuidade.
        var tenant = new FakeTenantContext(empresaId);
        builder.AddInterceptors(new SetEmpresaIdInterceptor(tenant));

        var context = new AppDbContext(builder.Options, tenant);
        context.Database.Migrate();
        return context;
    }

    /// <summary>
    /// Cria um <see cref="AppDbContext"/> em <b>modo central</b> (sem filtro global por empresa).
    /// Use só para preparação/inspeção cross-tenant — ex.: criar empresas reais, verificar dados de
    /// outra empresa ou limpar o banco inteiro.
    /// </summary>
    /// <remarks>
    /// <b>Somente leitura sobre dados de domínio.</b> O <see cref="SetEmpresaIdInterceptor"/> está
    /// instalado e, sem empresa selecionada, lança em qualquer <c>Added</c> de entidade de domínio —
    /// de propósito: uma linha gravada assim ficaria com <c>EmpresaId = Guid.Empty</c> e invisível
    /// para todos os tenants. Para criar dados de uma empresa, use
    /// <see cref="CreateDbContext(Guid?)"/> com o id dela (ou <c>EmpresaTestHelper</c>).
    /// </remarks>
    public AppDbContext CreateDbContextCentral() => CreateDbContext(null);

    /// <summary>
    /// Limpa todas as tabelas de domínio, de Identity e de auditoria, e recria a empresa padrão dos
    /// testes. O Postgres do AppHost usa um volume persistente (para sobreviver a restarts do
    /// `dotnet run` local), então diferentes classes de teste que usam esta fixture acabam
    /// compartilhando os mesmos dados entre execuções — cada teste deve chamar isto no seu
    /// <c>InitializeAsync</c> para garantir um estado limpo, independentemente do que outra
    /// classe de teste tenha deixado no banco.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Usa um <b>único</b> <c>TRUNCATE ... RESTART IDENTITY CASCADE</c> em vez de carregar as linhas
    /// e removê-las uma tabela por vez. A versão anterior (<c>RemoveRange</c> em ordem manual)
    /// dependia da ordem de delete estar exatamente certa e era a origem provável de uma falha
    /// intermitente de FK (<c>FK_RecorrenciasCompra_Produtos_ProdutoId</c>) quando o banco acumulava
    /// dados de classes de teste anteriores. O <c>TRUNCATE</c> não carrega linha nenhuma, não
    /// depende de ordem e é atômico.
    /// </para>
    /// <para>
    /// <b>O <c>CASCADE</c> é seguro porque a lista abaixo já cobre todas as tabelas com FK entre
    /// si</b>: as filhas (<c>ItensVenda</c>, <c>Pagamentos</c>, <c>MovimentacoesEstoque</c>,
    /// <c>HistoricosEstoque</c>, <c>PrecosPersonalizadosClientes</c>, <c>EmprestimosVasilhames</c>,
    /// <c>RecorrenciasCompra</c>, as tabelas de junção do Identity) referenciam
    /// <c>Vendas</c>/<c>Produtos</c>/<c>Clientes</c>/<c>Empresas</c>/<c>AspNetUsers</c>/
    /// <c>AspNetRoles</c>, que também estão listadas. Não existe tabela fora do conjunto com FK
    /// para alguma destas, então o <c>CASCADE</c> não esvazia nada que não deveria — em particular
    /// <c>__EFMigrationsHistory</c> (sem FKs) é preservada, senão o próximo <c>Migrate()</c>
    /// tentaria reaplicar o schema.
    /// </para>
    /// <para>
    /// Ao final, garante a linha da empresa padrão dos testes — <c>AspNetUsers.EmpresaId</c> tem FK
    /// Restrict para <c>Empresas</c>, então sem ela qualquer teste que crie usuário com empresa
    /// falharia.
    /// </para>
    /// </remarks>
    public async Task ResetDatabaseAsync()
    {
        using var db = CreateDbContextCentral();

        // Lista derivada das tabelas reais do schema (AppDbContext + Identity + auditoria), não do
        // código das entidades: se uma migração futura adicionar uma tabela, ela precisa entrar
        // aqui. `__EFMigrationsHistory` fica de fora de propósito.
        var tabelas = string.Join(",\n    ", TabelasDoReset.Select(t => $"\"{t}\""));

        // SQL montado por concatenação a partir de uma lista de nomes de tabela fixa do próprio
        // código (nunca de entrada do usuário) — daí a supressão do EF1003. Não há valor de
        // parâmetro aqui: identificadores de tabela não podem ser parametrizados.
#pragma warning disable EF1003 // Nomes de tabela são constantes do código, não entrada do usuário.
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE\n    " + tabelas + "\nRESTART IDENTITY CASCADE;");
#pragma warning restore EF1003

        // Empresa padrão dos testes: precisa existir ANTES de qualquer teste criar usuário com
        // `EmpresaId` (FK Restrict de AspNetUsers). Inserida por SQL direto porque o Id é o GUID
        // fixo de TenantIntegracao (a entidade gera o Id no construtor).
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Empresas" ("Id", "Identificador", "Nome", "Telefones", "Ativo", "CriadaEm")
            VALUES ({0}, {1}, {2}, '', true, {3})
            """,
            TenantIntegracao.EmpresaId,
            IdentificadorEmpresaPadrao,
            "Empresa Padrão dos Testes",
            DateTime.UtcNow);
    }
}