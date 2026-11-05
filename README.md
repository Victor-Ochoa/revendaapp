# Revenda.App

Sistema de gestão para revendas, **multi-empresa (multi-tenant)**: clientes (com controle de
fiado/saldo devedor), estoque, vendas e dashboards gerenciais. O primeiro cliente é uma
revendedora de gás de cozinha e água mineral — o domínio de vendas, fiado e vasilhames foi
modelado a partir dela.

Além do fluxo básico de venda, o app cobre **controle de vasilhames** (cheios, vazios disponíveis e
emprestados, com troca × empréstimo na venda), **aviso de recompra** aprendido por cliente/produto
e **edição/exclusão de vendas** com estorno (aba Vendas em `/configuracoes`, restrita a
administradores). Ver "Funcionalidades além do fluxo básico" no [AGENTS.md](AGENTS.md).

Cada empresa (tenant) tem nome, cor e logo próprios e enxerga **apenas** os seus dados. A
marca do produto (Revenda.App) aparece no login e no modo central, onde o super administrador
gerencia as empresas.

## Stack
- **.NET 10** / C#
- **.NET Aspire** — orquestração local (Postgres, dashboard de telemetria)
- **Blazor Web App** (Interactive Server) + **MudBlazor** — front-end
- **EF Core** + **PostgreSQL** — persistência
- **Multi-tenancy**: filtro global por `EmpresaId` em cada entidade de domínio no `AppDbContext`
  + `SetEmpresaIdInterceptor` (interceptor de `SaveChanges`) carimbando o `EmpresaId` das linhas
  novas. O `AppDbContext` é **scoped**, não pooled — ver a seção de multi-empresa no
  [AGENTS.md](AGENTS.md).
- **ASP.NET Core Identity** — autenticação (papéis `SuperAdmin`, `Admin` e `Vendedor`)
- **Mediator** (martinothamar) + **FluentValidation** — casos de uso (vertical slices)
- **xUnit** — testes unitários e de integração (TDD)

## Estrutura
```
src/
  RevendaApp.AppHost/          Orquestração Aspire (Postgres, projeto Web)
  RevendaApp.ServiceDefaults/  Telemetria, health checks, resiliência
  RevendaApp.Domain/           Entidades e enums de domínio
  RevendaApp.Infrastructure/   EF Core (AppDbContext), Identity, migrations
  RevendaApp.Web/              Blazor Web App + MudBlazor (Features/ = vertical slices)
tests/
  RevendaApp.UnitTests/        Testes unitários (handlers, validators, cálculos)
  RevendaApp.IntegrationTests/ Testes de integração (Aspire.Hosting.Testing + Postgres real)
```

## Como rodar
Pré-requisitos: .NET 10 SDK (ver [global.json](global.json)) e Docker (para o container do
Postgres via Aspire).

O AppHost declara os parâmetros `seed-admin-email` e `seed-admin-password` **sem valor padrão**
(`builder.AddParameter(...)` em `AppHost.cs`) — eles são obrigatórios em qualquer execução do
AppHost, inclusive em desenvolvimento. Configure-os **antes** do primeiro `dotnet run`, via user
secrets do projeto AppHost (recomendado; fica fora do repositório):

```bash
cd src/RevendaApp.AppHost
dotnet user-secrets set "Parameters:seed-admin-email" "admin@revenda.app.com.br"
dotnet user-secrets set "Parameters:seed-admin-password" "<senha-forte>"
cd ../..
```

Alternativas ao user secrets: a seção `Parameters` no `appsettings.Development.json` do AppHost
(versionado — não coloque senha real) ou as variáveis de ambiente
`Parameters__seed_admin_email` / `Parameters__seed_admin_password` (o `__` marca a seção e o `-`
do nome vira `_`). Depois:

```bash
dotnet run --project src/RevendaApp.AppHost
```

Isso abre o **Aspire Dashboard** com os logs/telemetria de todos os recursos, incluindo o
container do Postgres e a aplicação web.

> **Se os parâmetros não estiverem configurados**, o AppHost **não falha com erro**: os recursos
> `seed-admin-email`/`seed-admin-password` ficam no estado `ValueMissing`, o recurso `web` fica em
> `Waiting` ("Waiting for value from resource 'seed-admin-email'") e nada sobe. O dashboard oferece
> um formulário "Unresolved parameters" para informar os valores — é o mesmo mecanismo do user
> secrets. O comando `aspire describe` mostra esse estado.

### Bootstrap: super admin da central × empresa demo
Na inicialização o `RevendaApp.Web` aplica as migrations e roda dois seeds independentes:

- **Super admin da central** (`Features/Auth/SeedData.cs`): cria **um** usuário na role
  `SuperAdmin`, **sem empresa** (`EmpresaId = null`). É ele que acessa `/central`. As credenciais
  vêm de `SeedAdmin:Email` / `SeedAdmin:Password`, que o AppHost repassa a partir dos parâmetros
  obrigatórios `seed-admin-email` / `seed-admin-password` (ver a seção anterior). O fallback de
  desenvolvimento do `SeedData` (`admin@revenda.app.com.br` / `RevendaApp@123`) **não é alcançável pelo
  AppHost** — só vale quando o `RevendaApp.Web` é hospedado sem ele (ex.: testes que constroem o host
  diretamente). **Em Produção não há fallback**: se as duas chaves não estiverem configuradas, o
  seed lança e a aplicação não sobe.
  Este seed **não** cria empresa nem admin de empresa.
- **Empresa demo** (`Features/Seed/SeedDemoData.cs`): cria a empresa `Demo Ltda` e o admin dela
  (`admin@demo.revenda.app.com.br` / `Demo@123`, role `Admin`), mais clientes, produtos, vendas,
  pagamentos e movimentações de estoque de demonstração. É idempotente e roda **somente fora de
  Produção**, controlado por `Seed:EnableDemoData` (default: `!IsProduction()`; em
  `appsettings.Production.json` está `false`).

### Importante para volumes antigos do Postgres
O Postgres do AppHost usa volume persistente (`.WithDataVolume()`), e o schema mudou para
multi-empresa na migração `MultiEmpresa`. Um volume criado **antes** dessa mudança tem dados
legados cujo `EmpresaId` foi preenchido com `Guid.Empty` — essas linhas não pertencem a nenhuma
empresa (aparecem só no modo central, nunca na empresa). Para um ambiente limpo, recrie o volume:

```bash
docker volume ls | grep revendaapp            # descubra o <hash>
docker volume rm revendaapp.apphost-<hash>-postgres-data
```

A senha do usuário `postgres` é **fixa** no AppHost (`postgres-password`, valor
`revendaapp-dev-only`) justamente por causa desse volume — **não remova** essa senha fixa, senão o
Aspire gera uma senha nova a cada execução e o volume antigo passa a recusar a autenticação.

## Deploy
O deploy em produção (`deploy/README.md`) usa `aspire deploy -e Production` para uma VM com Caddy
na frente (TLS). Há também o workflow `.github/workflows/deploy.yml`.

## Testes
```bash
dotnet test
```

Formatação (mesmo check do CI): `dotnet format RevendaApp.slnx --no-restore --verify-no-changes`.

A suíte de integração (`tests/RevendaApp.IntegrationTests`) sobe um **Postgres real via Aspire**
(nada de mocks de banco) e roda **serializada**: o `AssemblyInfo.cs` declara
`[assembly: CollectionBehavior(DisableTestParallelization = true)]`, porque cada classe sobe seu
próprio container e rodar em paralelo causa falhas transitórias de conexão. Rodar só os
unitários (rápido, sem Docker): `dotnet test tests/RevendaApp.UnitTests`.

## Primeiros passos na aplicação
1. Entre com o **super admin da central** (o e-mail/senha que você configurou em
   `Parameters:seed-admin-email` / `Parameters:seed-admin-password`) — o login dele redireciona
   para `/central`.
2. Em `/central`, clique em **Nova empresa** e informe os dados da empresa **e do primeiro
   administrador** dela (nome, e-mail e senha) — os dois são criados na mesma transação.
3. Use **Entrar como** na linha da empresa para assumir a sessão do admin dela; a partir daí você
   navega nas telas de domínio (`/`, `/clientes`, `/estoque`, `/configuracoes`) já com os dados
   daquela empresa.
4. Configure produtos em `/estoque/novo` (mutações de estoque e a página `/configuracoes` são
   restritas a administradores).
5. Para sair do modo empresa, use **Voltar à central** no menu do usuário.

Veja também [AGENTS.md](AGENTS.md) para as convenções de desenvolvimento, arquitetura e as
regras de multi-tenancy do projeto.