# AGENTS.md — Contexto para agentes de IA

Este arquivo orienta qualquer agente de IA (Copilot, Claude, Cursor, etc.) trabalhando neste
repositório. Leia isto antes de propor ou aplicar mudanças.

## Sobre o projeto
O produto é o **Revenda.App** — sistema de gestão para revendas, **multi-empresa (multi-tenant)**.
Cada empresa (tenant) é uma revenda com dados isolados; o primeiro cliente é a "André Gas e Água",
uma revendedora de gás de cozinha e água mineral, de onde veio o modelo de domínio: clientes com
fiado/saldo devedor, estoque, vendas, vasilhames e dashboards gerenciais de vendas diárias/mensais,
estoque e lucro. O nome antigo dos projetos (`AndreGas.*`) foi renomeado para `RevendaApp.*` no
rebranding. A **marca do produto é fixa** (Revenda.App,
inclusive na tela de login e no modo central); cada empresa define o próprio **nome, cor primária e
logo**, exibidos no app bar. Cores padrão do tema: **azul marinho e branco**.

## Stack e decisões de arquitetura
- **.NET 10**, C# — SDK fixado em [global.json](/home/voch_silva/dev/opencode/revendaapp/global.json).
- **.NET Aspire** orquestra o ambiente local: Postgres roda como container gerenciado pelo
  `RevendaApp.AppHost`. Não configure connection strings manuais — use `builder.AddPostgres(...)` no
  AppHost e `builder.Configuration.GetConnectionString("revendaapp")` no `RevendaApp.Web`. O contexto
  é registrado com `AddDbContext` (scoped) + `EnrichNpgsqlDbContext` — **nunca**
  `AddNpgsqlDbContext` (ver "Multi-empresa" abaixo).
- **Blazor Web App** com render mode **Interactive Server** (não WebAssembly, não Auto). Não há
  projeto de API separado — os componentes Razor chamam os handlers diretamente via DI.
- **MudBlazor** para todos os componentes de UI. Tema customizado azul marinho + branco definido
  uma única vez (ver `MudTheme` em `RevendaApp.Web`).
- **Vertical Slice Architecture (VSA)**: funcionalidades ficam em `RevendaApp.Web/Features/<Area>/<CasoDeUso>/`,
  cada uma contendo, juntos: Command/Query, Handler, Validator (FluentValidation) e o(s)
  componente(s) `.razor`. **Não** criar camadas horizontais genéricas (`Services/`, `Controllers/`,
  `Repositories/` compartilhados) — cada slice acessa o `AppDbContext` diretamente dentro do seu
  handler.
- **Mediator** (pacote `Mediator.Abstractions` / `Mediator.SourceGenerator`, autor martinothamar) é
  o mediator usado para os casos de uso — **não usar MediatR v13+** (tornou-se pago/comercial).
  API é quase idêntica a MediatR (`IRequest<T>`, `IRequestHandler<TRequest, TResponse>`).
  Registrado com `AddMediator(o => o.ServiceLifetime = ServiceLifetime.Scoped)` — **não** usar o
  lifetime padrão (`Singleton`), pois handlers costumam depender de serviços scoped do EF
  Core/Identity (ex.: `SignInManager`), o que quebra a validação de DI em tempo de execução.
  Validação automática via `RevendaApp.Web/Common/Behaviors/ValidationBehavior.cs` (pipeline
  behavior que roda qualquer `IValidator<TMessage>` do FluentValidation antes do handler).
- **EF Core** + **PostgreSQL** via `Npgsql.EntityFrameworkCore.PostgreSQL`. Migrations vivem em
  `RevendaApp.Infrastructure`.
- **ASP.NET Core Identity** para autenticação, com três **papéis** (`ApplicationRole`):
  `SuperAdmin` (central, sem empresa), `Admin` (administrador de uma empresa) e `Vendedor`.
  `AppDbContext` estende `IdentityDbContext<ApplicationUser, ApplicationRole, Guid>`
  e registra o Identity via `AddIdentity<ApplicationUser, ApplicationRole>(...)` (com `UserManager`,
  `RoleManager` e `SignInManager`). `CookieAuthenticationOptions.LoginPath` é configurado para
  `/login` e `AccessDeniedPath` para `/sem-autorizacao` (página personalizada para usuário
  autenticado sem a role exigida) via `ConfigureApplicationCookie(...)`. **Não** chame
  `AddAuthentication().AddIdentityCookies()` de novo: o `AddIdentity` já registra o esquema
  `Identity.Application` e a segunda chamada estoura `Scheme already exists` no startup.
  O bootstrap cria **apenas o super admin da central** — role `SuperAdmin`, `EmpresaId = null` (ver
  `Features/Auth/SeedData.cs`; credenciais via `SeedAdmin:Email`/`SeedAdmin:Password`, com defaults
  de desenvolvimento `admin@revenda.app.com.br`/`RevendaApp@123`; em Produção o seed **lança** se as
  chaves não estiverem configuradas). As três roles também são criadas ali
  (`SeedData.GarantirRolesAsync`). Empresas e seus admins são criados depois, pela central.
  Exclusividade: a página `Configurações` (`/configuracoes`) e as **mutações de estoque** (cadastrar,
  editar, movimentar) são restritas a administradores. A autorização é camada em duas frentes:
  (a) **administrativa** via políticas `[Authorize(Policy = ...)]` nas rotas (o `Routes.razor`
  usa `RoleAwareNotAuthorized` para redirecionar autenticado-sem-role → `/sem-autorizacao` e
  não-autenticado → `/login`; o super admin da central que cai numa página de domínio é mandado
  para `/central`), e (b) **no servidor** nos handlers de mutação de estoque e de
  cadastro de usuário, que injetam `IUsuarioAutenticado` e chamam `RequerAdminAsync()` — esse é o
  limite real de segurança. A UI **não esconde** funcionalidades: links ficam visíveis e os botões
  restritos ficam **desabilitados** com tooltip "Apenas administradores" (ver `EstoqueLista.razor`).
  Usuários comuns (Vendedor) acessam dashboards, vendas, clientes e fiados; não acessam
  Configurações nem mutam estoque. A gestão de usuários vive em `Features/Usuarios`
  (`Configuracoes.razor` + `CadastrarUsuario...`), e a gestão de empresas/usuários de qualquer
  empresa vive na central (`Features/Central/*`).
- **Páginas de autenticação (Login) exigem SSR estático**: qualquer página Razor que precise
  gravar o cookie de autenticação via `SignInManager` (ex.: Login) **não pode** usar
  `@rendermode InteractiveServer` — o `HttpContext` só está disponível como
  `[CascadingParameter]` durante SSR estático. Por isso `Login.razor` não declara `@rendermode` e
  usa um layout próprio (`LoginLayout.razor`) em vez do `MainLayout`. Consequência importante:
  **os componentes de formulário do MudBlazor (`MudTextField`, `MudCheckBox` etc.) não funcionam
  em páginas SSR estáticas** (dependem de interatividade/JS interop) — use os componentes nativos
  do Blazor (`InputText`, `InputCheckbox`, `EditForm` + `DataAnnotationsValidator`) para qualquer
  campo de formulário em página estática; componentes MudBlazor puramente apresentacionais
  (`MudPaper`, `MudText`, `MudAlert`, `MudButton` com `ButtonType.Submit`) funcionam normalmente.
  Páginas comuns (dashboards, listas, modais) devem declarar `@rendermode InteractiveServer`
  (ver `Features/Home/Dashboard.razor`) para poderem usar os componentes interativos do MudBlazor
  normalmente.
- **Central Package Management**: todas as versões de pacotes NuGet ficam em
  [Directory.Packages.props](/home/voch_silva/dev/opencode/revendaapp/Directory.Packages.props) na
  raiz. Os `.csproj` referenciam pacotes **sem** atributo `Version`.
- **Migrations EF Core**: geradas a partir de `RevendaApp.Infrastructure` (tem seu próprio
  `IDesignTimeDbContextFactory` em `AppDbContextFactory.cs`, usado só em tempo de design — em
  runtime a connection string vem do Aspire). Requer a ferramenta `dotnet-ef` instalada
  (`dotnet tool install --global dotnet-ef`). Para gerar uma nova migração:
  `dotnet-ef migrations add NomeDaMigracao --project src/RevendaApp.Infrastructure --startup-project src/RevendaApp.Infrastructure -o Migrations`.
  As migrações são aplicadas automaticamente na inicialização do `RevendaApp.Web` (`Database.MigrateAsync()`).
  A migração `MultiEmpresa` adiciona `EmpresaId` (NOT NULL) às tabelas de domínio e a tabela
  `Empresas`; num banco já populado o `EmpresaId` das linhas existentes é preenchido com
  `Guid.Empty` (o default existe só para o `ADD COLUMN NOT NULL` e é removido logo depois) — esses
  dados **não pertencem a nenhuma empresa**. Por isso, em ambiente com volume antigo, recrie o
  volume do Postgres (`docker volume rm revendaapp.apphost-<hash>-postgres-data`) em vez de migrar
  dados legados. Veja também o gotcha da senha fixa do Postgres em "TDD — fluxo obrigatório".

## Multi-empresa (multi-tenant)

Leia esta seção inteira antes de mexer em qualquer handler, entidade ou consulta.

- **Produto × empresa.** O produto é o **Revenda.App** — marca fixa, exibida inclusive na tela de
  login (`IdentidadeEmpresaService.NomeProduto`). Cada empresa (entidade `Empresa`) tem
  `Identificador` (slug único e imutável), `Nome`, `CorPrimaria` e `Logo` próprios, exibidos no app
  bar via `IdentidadeEmpresaService` / `ITenantContext`. Sem empresa selecionada (modo central), a
  interface mostra a identidade do produto.
- **Como o isolamento funciona.** `ITenantContext` (scoped, implementação
  `RevendaApp.Web.Common.Tenancy.TenantContext`) lê o tenant das claims do principal autenticado;
  o `HttpContext.User` da requisição (`IHttpContextAccessor`) tem **precedência** sobre o
  `AuthenticationStateProvider` do circuito Blazor (fallback) — necessário porque no prerender SSR
  o provider pode não estar no escopo. O `AppDbContext` aplica **filtro global por `EmpresaId`** em
  **10** entidades de domínio (`Cliente`, `Produto`, `Venda`, `ItemVenda`, `Pagamento`,
  `MovimentacaoEstoque`, `HistoricoEstoque`, `PrecoPersonalizadoCliente`, `EmprestimoVasilhame`,
  `RecorrenciaCompra`) e o `SetEmpresaIdInterceptor` carimba `EmpresaId` nas linhas `Added` que
  ainda estejam com `Guid.Empty`. **Não** são filtradas: `Empresas`, as tabelas do Identity,
  `RegistrosAuditoria` (global, ver auditoria abaixo) e `DataProtectionKeys` (global por natureza:
  as chaves de criptografia valem para o processo inteiro, não para um tenant). A lista é
  **travada** por `tests/RevendaApp.UnitTests/Tenancy/FiltrosGlobaisTests.cs` — entidade nova
  com/sem filtro obriga a atualizar o teste e a decisão consciente.
- **Fail-closed.** `ITenantContext.EmpresaId` só devolve `null` (modo central = vê todas as
  empresas) em dois casos: (a) **não autenticado** (seed, SSR estático, design-time) ou (b)
  autenticado com a claim `EhSuperAdmin = true`. Um usuário **autenticado, não-super-admin e sem
  claim `EmpresaId`** é configuração inválida e `EmpresaId` **lança**. `EmpresaIdRequerida(acao)` é
  a **primeira instrução** de todo handler de domínio (e também no interceptor): a operação falha
  em vez de gravar/ler em modo central. Handlers da central (`Features/Central/*`) fazem o inverso
  e começam por `if (!tenant.EhSuperAdmin) recusar`.
- **Dois gotchas que NÃO podem ser desfeitos** (ambos custaram bug real de vazamento entre
  empresas):
  1. **`AppDbContext` scoped, nunca pooled.** O registro é `AddDbContext<AppDbContext>(...)` +
     `EnrichNpgsqlDbContext<AppDbContext>()` em `Program.cs`. **Não** volte para
     `AddNpgsqlDbContext<AppDbContext>()`: ele usa `AddDbContextPool`, e no pooling as
     opções/interceptores são montados uma vez a partir do provider **raiz** — o `ITenantContext`
     scoped fica capturado (vazio em Production, erro de escopo em Development) e o filtro passa a
     ver tudo.
  2. **`_empresaId` no `AppDbContext` é propriedade, nunca campo `readonly`.** O filtro fecha sobre
     `private Guid? _empresaId => tenant?.EmpresaId;`, reavaliada a cada query. Um campo `readonly`
     capturado no construtor congela o tenant do momento da construção — durante o prerender SSR o
     contexto é construído pelo `SecurityStampValidator` antes de o principal existir, e o mesmo
     contexto serviria a requisição inteira **vazando dados de todas as empresas** no HTML.
- **Papéis e políticas.** Três roles em `ApplicationRole`: `SuperAdmin` (central, sem empresa),
  `Admin` (admin de uma empresa), `Vendedor`. Políticas reais em
  `Common/Authorization/Politicas.cs`: `ApenasAdmin` (role `Admin`), `ApenasSuperAdmin` (role
  `SuperAdmin`) e `ApenasEmpresa` (exige a claim `EmpresaId` — impede o super admin de cair numa
  página de domínio). As páginas de domínio declaram `ApenasEmpresa`; `/central` declara
  `ApenasSuperAdmin`; `/configuracoes` e `/estoque/novo` declaram `ApenasAdmin` (o cadastro de
  produto declara as duas). As **modais** de mutação de estoque abertas a partir de `/estoque`
  dependem do handler, não da política da página.
  A política da rota é **ocultação/navegação**; o limite real é a checagem no handler
  (`IUsuarioAutenticado.RequerAdminAsync()` para admin de empresa, `ITenantContext.EhSuperAdmin`
  para a central).
- **Impersonation ("Entrar como").** `Features/Central/EntrarComo` reemite o cookie com o principal
  do admin da empresa, acrescentando as claims `Impersonando` e `SuperAdminOriginalId`. Como a
  troca de principal só vale numa requisição nova, **a navegação precisa ser de página completa**
  (`forceLoad: true`, ou form POST nativo como em `Central.razor`) — senão o circuito Blazor em
  andamento mantém o tenant/`AppDbContext` antigo. "Voltar à central"
  (`Features/Central/VoltarCentral`) valida o super admin original **apenas pela claim assinada
  `SuperAdminOriginalId`** (usuário que ainda seja super admin, ativo e sem empresa) — nunca aceita
  um id vindo do corpo/query, que seria forjável.
- **Auditoria.** `RegistroAuditoria` é **global**: sem filtro por empresa, sem interceptor, com
  `EmpresaId` **anulável** preenchido explicitamente pelo handler (`null` = ação da central). Toda
  leitura que atende uma empresa **precisa** filtrar explicitamente por `EmpresaId`; só a central
  consulta sem filtro (`Features/Central/Auditoria`). As ações da central gravam
  `Origem = RegistroAuditoria.Origens.Central` e as da empresa `Origens.App`. As ações auditadas
  estão em `RegistroAuditoria.Acoes`.
- **Testes de integração e tenant.** `DatabaseFixture.CreateDbContext()` é **tenant-bound**: devolve
  um contexto filtrado pela empresa padrão dos testes (`TenantIntegracao.EmpresaId`) com o
  interceptor apontando para ela. `CreateDbContextCentral()` é modo central e serve **só** para
  preparação/inspeção cross-tenant (criar empresas, verificar dados de outra empresa, resetar o
  banco) — gravar entidade de domínio por ele **lança** de propósito. Para outra empresa, use
  `CreateDbContext(Guid?)`. `EmpresaTestHelper` cria empresas e usuários adicionais (com sufixo
  aleatório no `Identificador`, já que o volume é persistente); a prova real de isolamento está em
  `TenancyIsolationTests`. Toda classe que usa a fixture chama `ResetDatabaseAsync()` no
  `InitializeAsync`.
- **Novas telas.** `/central` (super admin): abas **Empresas** (criar, configurar, usuários, ver
  produtos, inativar/ativar, "Entrar como") e **Auditoria**. A aba **"Minha Empresa"** em
  `/configuracoes` deixa o admin da empresa editar nome/cor/logo do próprio cadastro
  (`Features/ConfiguracaoEmpresa`). Em `/configuracoes` a ordem das abas é **Minha Empresa**,
  Usuários e Vendas — "Minha Empresa" é a **primeira** e a ordem é pinada por
  `ConfiguracoesAbasTests`. A cor primária é escolhida num `MudColorPicker` (`ShowAlpha=false`, o
  domínio não tem canal de alfa); a conversão para o hex do comando passa por `CorPrimaria` e o
  teste garante que o valor enviado é aceito pelo validador real.

## Comandos comuns
- **Rodar a aplicação local** (sobe Aspire + Postgres + Web, abre o Aspire Dashboard):
  `dotnet run --project src/RevendaApp.AppHost`. O bootstrap cria o **super admin da central**
  (role `SuperAdmin`, sem empresa) com as credenciais de `SeedAdmin:Email`/`SeedAdmin:Password`
  (ou os defaults de desenvolvimento `admin@revenda.app.com.br`/`RevendaApp@123`) — **não** é admin de
  empresa. Empresas são criadas pela central (`/central`).
- **Build**: `dotnet build`
- **Formatação**: `dotnet format RevendaApp.slnx --no-restore --verify-no-changes` (é o passo
  `Format check` do CI; rode antes de abrir MR).
- **Testes**: `dotnet test` (unitários + integração). Rodar só unitários:
  `dotnet test tests/RevendaApp.UnitTests` (rápido, InMemory). Rodar um teste específico:
  `dotnet test --filter FullyQualifiedName~NovaVendaFeatureTests`.
- **Seed de demonstração** (`Features/Seed/SeedDemoData.cs`): roda automaticamente no
  startup do `RevendaApp.Web` **somente fora de Produção** (`Seed:EnableDemoData`, default
  `!IsProduction()`; em `appsettings.Production.json` é `false`). É **idempotente** e cria a
  **empresa demo** (`Demo Ltda`) + o admin dela (`admin@demo.revenda.app.com.br`, role `Admin`) e,
  sob o tenant dessa empresa, dados de demonstração (clientes, produtos, vendas cobrindo todas as
  formas de pagamento, pagamentos e movimentações de estoque com datas anteriores a hoje) se ainda
  não houver produtos na empresa demo.
- **Migrations EF**: ver seção acima (exige `dotnet-ef`). O hardening de produção acrescentou as
  migrações `DataProtectionKeys`, `IndicesDashboard` e `ConcorrenciaXmin`. A `ConcorrenciaXmin`
  contém chamadas `AddColumn`/`DropColumn` no `Up`/`Down`, mas o SQL emitido é **vazio** — o Npgsql
  filtra operações em colunas de sistema (`xmin` é uma coluna de sistema do Postgres). Não tente
  "consertar" essa migração: as chamadas existem só para o snapshot do EF reconhecer o token.

## Funcionalidades além do fluxo básico

- **Vasilhames** (`Features/Estoque/Vasilhames` + `Features/Clientes/Emprestimos`): produto com
  `ControlaVasilhame` acompanha três contadores — cheios (`QuantidadeEstoque`), vazios disponíveis
  (`QuantidadeVasilhames`) e emprestados (`QuantidadeVasilhamesEmprestados`). `EmprestimoVasilhame`
  é a fonte da verdade de "quem tem quantos". Na venda, `ItemVenda.EhEmprestimo` distingue
  **troca** (vazio volta ao depósito) de **empréstimo** (vazio fica com o cliente e vira débito de
  vasilhame). Há empréstimo/devolução manuais (tela do cliente e aba do estoque) e a tela
  "Pátio"/controle de vasilhames (`ObterControleVasilhamesQuery` + `VasilhameGraficoBuilder`).
- **Recompra / recorrência** (`RecorrenciaCompra`, `Common/RecorrenciaCompraAprendizado.cs`):
  produto com `AvisarRecompra` e `DiasPadraoRecompra` aprende o intervalo médio entre compras por
  cliente (`IntervaloMedioDias`) e o dashboard lista quem passou da data prevista. O recálculo roda
  **depois** do `SaveChanges` da venda (precisa enxergar a venda nova) e exige um segundo save.
- **Editar/excluir venda** (`Features/Admin/Vendas`, restrito a `Admin`): a edição **reverte** os
  efeitos da venda original (saldo devedor fiado, estoque, pagamento no ato) e **recria** a venda
  pelo mesmo `RegistrarVendaCommandHandler.RegistrarAsync`, preservando a `DataHora` original e as
  regras de preço. A exclusão é **lógica** (`ExcluidaEm`) com estorno. Ambos chamam
  `ReversaoVendaHelper` e `RecorrenciaCompraAprendizado`, e disparam o `VendasAtualizadasNotifier`.
  Toda a tela vive na aba **Vendas** de `/configuracoes`.

## Helpers compartilhados (`RevendaApp.Web/Common`)

Antes de reimplementar, verifique se já existe:

| Helper | Papel |
|---|---|
| `TenantContext` / `ITenantContext` | Tenant por claims; `EmpresaIdRequerida` é a guarda de todo handler de domínio |
| `TransacaoComRetry` | Transação explícita compatível com a execution strategy de retry do Npgsql (`EnableRetryOnFailure`); obrigatório quando há `BeginTransaction` |
| `VendaQueries` | Consultas de venda compartilhadas (ex.: última entrega por cliente, sem N+1) |
| `VendaItemCalculo` / `Venda` | Rateio de desconto e lucro por item |
| `RecorrenciaCompraAprendizado` | Cálculo do intervalo de recompra |
| `RegraDeAcesso` | Regra única "usuário ativo + empresa ativa", usada no login e na recarga de sessão |
| `DestinoSeguro` | Validação de `returnUrl` (fecha open redirect) |
| `ValidadeAbsolutaImpersonacao` | Teto absoluto de 8h da sessão impersonada, via claim imutável |
| `RenovacaoPrincipalImpersonacao` | Preserva as claims de impersonation nas reemissões do principal |
| `BrasilTimeZone` | Conversão UTC ↔ dia civil de Brasília (dashboard, histórico) |
| `UsuarioAtual` | Nome/Id do usuário para histórico de estoque e `Venda.VendedorId` |
| `EnumRotulos` | Rótulos pt-BR dos enums |
| `IdentidadeEmpresaService` | Nome/cor/logo exibidos no app bar (memoiza o logo) |
| `CorPrimaria` (`Features/ConfiguracaoEmpresa`) | Conversão `MudColor` ↔ hex `#RGB`/`#RRGGBB` que o validador aceita; `EhHexValido` responde "isto é hex?" (o validador trata cor vazia como opcional) |
| `AddPipelineBehaviors()` (`Common/Behaviors`) | Registro único dos `IPipelineBehavior` técnicos, na ordem que define o pipeline — chamado por `Program.cs` e pelo teste de ordem |
| `SemaforoDoCircuito` / `SerializacaoDbContextBehavior` (`Common/Behaviors`) | Portão único do `AppDbContext` do circuito: serializa todo `Send` para que dois componentes nunca usem o contexto ao mesmo tempo (reentrante via `AsyncLocal`) |

## Refresh de telas em tempo real (Blazor Server)
Telas que exibem dados de vendas (ex.: Dashboard) reagem a mudanças sem recarregar a página
via o serviço scoped `RevendaApp.Web.Common.VendasAtualizadasNotifier`. Handlers que alteram o
estado de vendas (ex.: `RegistrarVendaCommandHandler`, `RegistrarPagamentoCommandHandler`)
chamam `notifier.NotificarVendaRegistrada()` **aguardando a propagação** (os inscritos rodam
sequencialmente), o que também dá tempo a consultas do mesmo circuito de usarem o mesmo
`DbContext` scoped sem `InvalidOperationException`. Use esse mesmo padrão em novos handlers
que mutem vendas.

## TDD — fluxo obrigatório

Este projeto é desenvolvido com TDD. Para qualquer novo caso de uso/handler:
1. Escreva primeiro o teste (unitário para lógica de handler/validator com mocks/InMemory;
   de integração quando envolver fluxo completo contra Postgres real via `Aspire.Hosting.Testing`).
2. Rode o teste e confirme que falha.
3. Implemente o mínimo necessário para o teste passar.
4. Refatore mantendo os testes verdes.
- Testes unitários: `tests/RevendaApp.UnitTests`. Validators e handlers que usam `AppDbContext`
  são testados com o provider **EF Core InMemory** (`RevendaApp.UnitTests.TestHelpers.InMemoryDbContextFactory`)
  — rápido e sem depender de container, mas suficiente para validar a lógica dos handlers.
- Testes de integração: `tests/RevendaApp.IntegrationTests` — sobem o Postgres real via Aspire
  (nada de mocks de banco em testes de integração). Duas fixtures reutilizáveis (`IClassFixture`):
  - `WebAppFixture`: sobe a distribuição inteira (Postgres + `RevendaApp.Web`) e expõe um
    `HttpClient` por teste — usada para fluxos ponta a ponta via HTTP (ex.: `LoginFlowTests`).
  - `DatabaseFixture`: sobe **só** o recurso Postgres do mesmo `RevendaApp.AppHost` (sem subir o
    processo `RevendaApp.Web`) e expõe `CreateDbContext()` — **tenant-bound**, já filtrado pela
    empresa padrão dos testes (`TenantIntegracao.EmpresaId`) e com o `SetEmpresaIdInterceptor`
    apontando para ela. `CreateDbContextCentral()` é o modo central (sem filtro) e serve só para
    preparação/inspeção cross-tenant; `CreateDbContext(Guid?)` age como outra empresa. Mais rápida,
    usada para testar handlers diretamente em processo (ex.: `ClientesFeatureTests`,
    `EstoqueFeatureTests`, `NovaVendaFeatureTests`, `PagamentoFeatureTests`, `DashboardFeatureTests`).
    Cada classe de teste implementa `IClassFixture<DatabaseFixture>, IAsyncLifetime`. **Importante**:
    como o Postgres usa volume persistente (ver gotcha abaixo), classes de teste diferentes acabam
    compartilhando os mesmos dados entre execuções — toda classe que usa `DatabaseFixture` deve
    chamar `await fixture.ResetDatabaseAsync()` no seu `InitializeAsync` (limpa todas as tabelas
    na ordem correta de FKs e recria a empresa padrão), em vez de limpar só as tabelas da própria
    feature. Ver também "Multi-empresa" acima para `EmpresaTestHelper` e `TenancyIsolationTests`.
  - A suíte roda com `[assembly: CollectionBehavior(DisableTestParallelization = true)]`
    (`AssemblyInfo.cs`) porque cada classe de teste sobe seu próprio container Postgres —
    rodar em paralelo sobrecarrega o ambiente e causa falhas transitórias de conexão.
  - **Gotcha**: o Postgres do AppHost usa `.WithDataVolume()` (dados persistem entre execuções,
    intencional para `dotnet run` local). Se os testes de integração começarem a **travar/dar
    timeout na inicialização da fixture** sem motivo aparente (ex.: após uma execução de testes
    interrompida no meio), o volume Docker pode ter ficado travado/corrompido — resolva com
    `docker volume rm revendaapp.apphost-<hash>-postgres-data` (veja o nome exato com
    `docker volume ls`) e rode os testes novamente.
  - **Gotcha (senha do Postgres)**: por causa do volume persistente acima, o AppHost fixa a senha
    do usuário `postgres` via `builder.AddParameter("postgres-password", secret: true, value: "revendaapp-dev-only")`
    em [AppHost.cs](/home/voch_silva/dev/opencode/revendaapp/src/RevendaApp.AppHost/AppHost.cs) — **não remova essa senha fixa**. Sem ela, o Aspire gera uma senha
    aleatória a cada `dotnet run`, mas o volume já tem o usuário `postgres` inicializado com a
    senha da execução anterior — o resultado é `password authentication failed for user
    "postgres"` nos logs do container. Se isso acontecer mesmo com a senha fixa (ex.: após trocar
    o valor do parâmetro), remova o volume (`docker volume rm revendaapp.apphost-<hash>-postgres-data`)
    para reinicializar o Postgres com a senha atual.
  - **Gotcha (rendermode misto no layout compartilhado)**: `MainLayout.razor` é o layout padrão
    de todas as rotas (incluindo `Login.razor`, que é SSR estática por precisar de `HttpContext`
    via `SignInManager`). Por isso `MainLayout.razor` **não pode** declarar `@rendermode
    InteractiveServer` no próprio arquivo — isso quebra a transição SSR→interativo ao navegar da
    página de login (`InvalidOperationException` sobre passar o parâmetro `Body` do tipo
    `RenderFragment` entre fronteiras de rendermode). A solução: extrair os elementos que
    precisam de interatividade (botão "Nova Venda" + menu do usuário) para um componente próprio
    ([TopMenuActions.razor](/home/voch_silva/dev/opencode/revendaapp/src/RevendaApp.Web/Components/Layout/TopMenuActions.razor))
    que declara seu próprio `@rendermode InteractiveServer` (incluindo `MudPopoverProvider`,
    `MudDialogProvider` e `MudSnackbarProvider`, pois esses providers precisam estar no mesmo
    contexto interativo do componente que os invoca). O `MainLayout.razor` em si fica sem
    rendermode fixo, permitindo hospedar tanto páginas estáticas (Login) quanto páginas
    interativas (Dashboard, Clientes, Estoque etc., que já declaram `@rendermode
    InteractiveServer` individualmente).

## Modelo de domínio (regras de negócio essenciais)
- **Cliente**: `Telefone` é a **chave natural única dentro da empresa** (índice único
  `(EmpresaId, Telefone)`) — é assim que o operador localiza o cliente na tela de venda. Duas
  empresas podem ter o mesmo telefone. `Endereco` é obrigatório. `SaldoDevedor` começa zerado.
- **Produto**: tem três preços — `PrecoVenda` (normal), `PrecoCusto` (para cálculo de lucro) e
  `PrecoGasDoPovo` (preço subsidiado, usado quando a forma de pagamento é "Gás do Povo"). Tem também
  `Ativo` (default true) com `Desativar()/Ativar()`: produto desabilitado **não aparece na Nova Venda**
  nem no alerta de estoque baixo do dashboard, mas permanece na listagem de estoque (marcado Inativo).
  Na tela de estoque, o botão **Editar** abre uma modal pra alterar nome/tipo/preços/estoque mínimo e
  um switch para desativar/reativar (`AtualizarProdutoCommand` + `ObterProdutoQuery`). `ListarProdutosQuery`
  aceita `IncluirInativos` (default `false` = só ativos); a tela de estoque lista todos, a Nova Venda só ativos.
- **Histórico de estoque (auditoria)**: cada produto tem uma modal **Histórico** (botão na listagem) que
  mostra `HistoricosEstoque` — como o produto foi criado, movimentado (entrada/saída/ajuste), editado
  (com **antes → depois** dos campos) e vendido. Cada registro tem tipo, descrição, motivo e o **usuário**
  autenticado (via `AuthenticationStateProvider`; "sistema" quando não autenticado). A venda **consolida**
  os itens do mesmo produto numa única linha ("Venda de N produtos") e grava `Venda.VendedorId` (registro).
  `MovimentacaoEstoque` continua controlando o estoque; `HistoricoEstoque` é apenas exibição.
- **Formas de pagamento** (`FormaPagamento`): `Pix`, `Debito`, `Credito`, `Dinheiro`, `Fiado`,
  `GasDoPovo`.
  - `Fiado` → soma o `ValorTotal` da venda ao `SaldoDevedor` do cliente.
  - `GasDoPovo` → pago integralmente no ato (**não** gera saldo devedor), mas usa
    `Produto.PrecoGasDoPovo` como preço unitário dos itens em vez de `PrecoVenda`.
  - Demais formas → pagas no ato, sem afetar o saldo devedor.
- **Desconto na venda**: campo único por venda (não por item), começa zerado, opcional.
  `ValorTotal = ValorBruto − Desconto + ValorEntrega`. É o `ValorTotal` (já líquido, com a entrega)
  que é somado ao saldo devedor quando `Fiado`. O desconto é **rateado proporcionalmente entre os
  itens**, reduzindo o `Lucro` líquido de cada item (mantém-se também um `LucroBruto` de referência
  sem desconto).
- **Valor de entrega**: campo único por venda (não por item), começa zerado, opcional, cobrado
  pela entrega do pedido. **Entra no faturamento**: `ValorTotal = ValorBruto − Desconto +
  ValorEntrega` (fiado soma com entrega ao saldo devedor; demais formas pagam no ato incluindo a
  entrega). **Não compõe o lucro** (não é item). Na Nova Venda, ao carregar o cliente é **sugerido
  o valor de entrega da última venda** daquele cliente (0 se ainda não houve venda ou se a última
  não teve entrega).
- **Lucro**: por item = `(PrecoUnitario − PrecoCustoUnitario) × Quantidade` menos a parcela
  rateada do desconto. Exibido no detalhe da venda, no histórico do cliente e agregado nos
  gráficos de lucro do dashboard.
- **Fluxo de Nova Venda** (modal global acessível pelo menu superior em todas as telas):
  1. Operador digita o telefone do cliente.
  2. Se existe → carrega nome/endereço/saldo automaticamente (somente leitura nesta tela).
  3. Se não existe → formulário inline exige Nome, Telefone e Endereço (todos obrigatórios) e
     cadastra o cliente com saldo zerado.
  4. Operador adiciona produtos/quantidades e, opcionalmente, desconto e valor de entrega
     (esse pré-preenchido com o último valor usado pelo cliente).
  5. Ao confirmar: cria `Venda` + `ItemVenda`(s), baixa estoque (`MovimentacaoEstoque` tipo Saída),
     e atualiza `SaldoDevedor` do cliente somente se `FormaPagamento == Fiado` (com entrega
     incluída no total).

## Convenções de código
- Nomes de domínio, telas e mensagens ao usuário em **português** (é o idioma do negócio); nomes
  de tipos/namespaces em C# seguem PascalCase normal do .NET.
- Nullable reference types habilitado; evite `null!` — prefira modelagem explícita.
- Helper que precisa ficar `internal` mas é provado por teste unitário é exposto a
  `RevendaApp.UnitTests` via `<InternalsVisibleTo>` em `RevendaApp.Web.csproj` — não torne o helper
  `public` só para testá-lo.
- Cada slice deve ter testes antes da implementação (ver seção TDD acima).
- Após cada fase de desenvolvimento concluída e validada, gerar um commit com mensagem no padrão
  [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `chore:`, `test:`).

## O que evitar
- Não usar MediatR versão 13 ou superior (pago). Usar o pacote `Mediator` (martinothamar).
- Não criar camadas de repositório genéricas compartilhadas entre slices.
- Não hardcodar connection strings — sempre via Aspire.
- Não aumentar o número de roles além de `SuperAdmin`/`Admin`/`Vendedor` sem necessidade; persistir
  verificações de autorização apenas como ocultação de UI — os handlers devem sempre reforçar com
  `RequerAdminAsync()` (empresa) ou `EhSuperAdmin` (central).
- **Não** voltar o `AppDbContext` para `AddNpgsqlDbContext` (pooling) nem transformar `_empresaId`
  em campo `readonly` — ver os dois gotchas em "Multi-empresa". Ambos causam vazamento entre
  empresas.
- **Não** gravar entidade de domínio (as 10 filtradas) sem empresa selecionada, nem ler
  `RegistrosAuditoria` sem filtro por `EmpresaId` fora da central. Toda entidade de domínio nova
  precisa de `EmpresaId` + filtro global + entrada no interceptor + atualização de
  `FiltrosGlobaisTests`.
- **Não** remova o `PersistKeysToDbContext<AppDbContext>()`: é o que mantém cookies de autenticação
  e tokens antiforgery válidos depois de um restart do container e o que permite que mais de uma
  réplica compartilhe as mesmas chaves. As chaves ficam na tabela global `DataProtectionKeys` (sem
  `EmpresaId`, sem filtro de tenant). Sem ele as chaves vão para o filesystem efêmero do container,
  cada restart gera chaves novas e **todo mundo é deslogado**; com duas réplicas, a sessão quebra ao
  trocar de instância.
- **Não** remova o `UseForwardedHeaders` nem o `ForwardedHeadersConfig`: é o que faz o app enxergar
  o esquema real (`https`) atrás do Caddy — sem ele o `UseHttpsRedirection`, as URLs absolutas e
  redirects gerados saem com `http`, e o HSTS não é aplicado. A confiança é loopback + RFC1918
  porque o Caddy no host alcança uma **porta publicada do container**, então o Kestrel vê o gateway
  da bridge do Docker, não o loopback. Ajustável por `Proxy:RedesConfiaveis`; um CIDR malformado
  **lança no startup** de propósito — um typo não pode alargar a confiança silenciosamente. O
  cookie de sessão não depende disso: fora de Development o `SecurePolicy` é `Always`, que é
  justamente o que impede um `X-Forwarded-Proto` forjado de rebaixar a sessão para texto puro.
- **Não** remova o `builder.Property<uint>("xmin").IsRowVersion()` de `Produto`/`Cliente`: é a única
  barreira contra duas vendas simultâneas do último item gravarem estoque negativo e contra dois
  pagamentos concorrentes sobrescreverem o `SaldoDevedor`. `UseXminAsConcurrencyToken()` não existe
  mais no Npgsql 10 — use `IsRowVersion()`.
- **Armadilha nova (o token muda tudo):** como `Produto`/`Cliente` agora têm token de linha, qualquer
  `ExecuteUpdate`/`ExecuteUpdateAsync` nessas tabelas grava por fora do change tracker e deixa a
  entidade rastreada com `xmin` defasado — o próximo save no mesmo contexto falha com um conflito
  **legítimo** (já quebrou um teste de integração). Use os métodos de domínio sobre a entidade
  rastreada (`produto.AjustarVasilhames(n)` + `SaveChangesAsync`) em vez de update em massa.
- **Não** remova o `ConcorrenciaBehavior` nem o `db.ChangeTracker.Clear()` dele: o `AppDbContext` é
  scoped ao circuito Blazor, o EF **não** desfaz o estado das entidades num save que falhou, e os
  diálogos mantêm o circuito vivo depois de exibir o erro. Sem a limpeza, a próxima operação do
  mesmo circuito commitaria a venda/pagamento fantasma da tentativa que o operador acreditou ter
  falhado.
- **Ordem do pipeline do Mediator:** o **primeiro** `IPipelineBehavior` registrado é o **mais
  externo** (o Mediator inverte a lista ao montar o encadeamento). A ordem é
  `SerializacaoDbContextBehavior` → `ConcorrenciaBehavior` → `ValidationBehavior`:
  `ConcorrenciaBehavior` vem antes de `ValidationBehavior` de propósito porque o conflito só nasce
  no save do handler e precisa envolver o pipeline inteiro; `SerializacaoDbContextBehavior` fica
  por fora dos dois porque o portão do `DbContext` tem de cobrir validação **e** handler, e o
  `ChangeTracker.Clear()` do conflito precisa rodar antes de o portão ser liberado. **O registro não
  fica em `Program.cs`**: vive em
  `Common/Behaviors/PipelineDeMediatorExtensions.AddPipelineBehaviors()`, chamado pelo `Program.cs` e
  pelo teste que pina a ordem. Reordene lá dentro — e rode o teste, que fica vermelho se a ordem
  mudar.
- **Um `AppDbContext` por circuito ⇒ operações do Mediator são serializadas.** O EF proíbe duas
  operações simultâneas no mesmo contexto (`InvalidOperationException`: "A second operation was
  started on this context instance"). Como o `AppDbContext` é scoped ao circuito Blazor, **dois
  componentes que consultam no próprio `OnInitializedAsync` colidem**: o render do filho acontece
  durante o `await` do pai. Foi assim que a aba "Minha Empresa" (que consulta) derrubou
  `/configuracoes` ao virar a aba ativa inicial — a aba anterior era markup inline, sem consulta.
  `Common/Behaviors/SemaforoDoCircuito` (scoped, `SemaphoreSlim(1,1)`) +
  `SerializacaoDbContextBehavior` (mais externo do pipeline) fecham isso para **todo** `Send`.
  A marca de reentrância é `AsyncLocal`, não campo: handler → `VendasAtualizadasNotifier` →
  query do Dashboard é a mesma cadeia, e esperar por ela seria deadlock. **Não** troque o
  `AppDbContext` por um contexto por operação: o filtro global fecha sobre
  `ITenantContext.EmpresaId` e o change tracker precisa ser o mesmo entre validação e save.
  Sintoma de que alguém furou o portão: `AppDbContext` concorrente com `Send` disparado fora do
  Mediator (ex.: leitura síncrona em propriedade de serviço scoped, como
  `IdentidadeEmpresaService.Logo`) durante um `await` de handler.
- **Atualizar pacotes não é só `Directory.Packages.props`.** `dotnet list package --outdated` é
  **cego a referências de MSBuild SDK**: o `Sdk="Aspire.AppHost.Sdk/<versão>"` em
  `RevendaApp.AppHost.csproj` não aparece nele (já ficou uma versão atrás sem ninguém notar). Ao
  subir a família Aspire, suba o SDK junto e confira o `Aspire.Hosting.AppHost` resolvido em
  `obj/project.assets.json`. As famílias EF Core e de criptografia sobem **coesas** — versões
  misturadas geram NU1605/NU1902, que são erro de build aqui.
