# Área de Relatórios — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Corrigir a quitação parcial de fiado, normalizar o legado no bootstrap e entregar a área `/relatorios` com quatro relatórios mensais (estoque, vendas, fiado, vasilhames), tudo em um único MR.

**Architecture:** VSA como o resto do projeto: cada relatório é um slice em `Features/Relatorios/<Relatorio>/` com Query + Handler + página `.razor` no mesmo diretório. A lógica de mês civil fica num único helper (`PeriodoMes`) e a regra de lucro reconhecido do fiado sai do handler do dashboard para `Common/VendaLucro`, usada por dashboard e relatórios. A quitação parcial vira estado de domínio em `Venda` (`ValorPagoFiado`/`SaldoFiado`/`AbaterFiado`), com migração e uma tarefa de bootstrap idempotente que reconstrói a alocação antiga a partir de `Pagamento.VendaId == null`.

**Tech Stack:** .NET 10, C#, Blazor Web App (Interactive Server), MudBlazor 9.12.0, Mediator (martinothamar), EF Core 10 + Npgsql, FluentValidation, xUnit, EF Core InMemory (unitários), Aspire + Postgres real (integração).

**Spec:** `docs/superpowers/specs/2026-11-17-area-relatorios-design.md`

## Global Constraints

- Nomes de domínio, telas e mensagens ao usuário em **pt-BR**; tipos/namespaces em PascalCase.
- Todo handler de domínio começa com `_ = tenant.EmpresaIdRequerida("<ação>")`. Modo central **lança**, nunca vê todas as empresas.
- Nenhum código de relatório usa `IgnoreQueryFilters()`. Nenhuma query aceita `EmpresaId` como parâmetro vindo da UI.
- `AppDbContext` é scoped e nunca pooled; `_empresaId` é propriedade, nunca campo `readonly`.
- Nunca `ExecuteUpdate`/`ExecuteUpdateAsync` em `Produto`/`Cliente` (token `xmin` defasa o change tracker). Use métodos de domínio sobre a entidade rastreada.
- Pacotes sem `Version` nos `.csproj` — versões só em `Directory.Packages.props`. Nenhuma dependência nova nesta entrega.
- Nullable reference types habilitado; sem `null!`.
- TDD: teste falhando → implementação mínima → verde → refactor. Teste nomeado pelo comportamento (`should ...`/`Handle_...`).
- Cada commit: `dotnet build` limpo, `dotnet test` verde, `dotnet format RevendaApp.slnx --no-restore --verify-no-changes` limpo.
- Commits em Conventional Commits, subject ≤ 72 caracteres, lowercase. Sem `git add .` — sempre arquivos explícitos.
- Não commitar em `main`: branch `feat/area-relatorios` primeiro.
- Migrações geradas a partir de `src/RevendaApp.Infrastructure` (ver "Comandos comuns" do `AGENTS.md`).
- `BrasilTimeZone` é a única fonte de conversão UTC↔Brasília. Nenhum handler monta offset na mão.

## Review Focus

Failure modes que o spec implica e que os testes de cada task precisam pinar:

1. **Pagamento que excede a dívida total** — o excedente não pode ser alocado a nenhuma venda nem reduzir `ValorPagoFiado` abaixo de zero. Teste em Task 2 e Task 3.
2. **Duas vendas fiado com pagamento que cobre uma e parte da outra** — FIFO estrito, a segunda fica aberta com o valor parcial. Teste em Task 2.
3. **Venda fiado excluída logicamente** — não pode receber alocação na normalização nem entrar no aging. Teste em Task 3 e Task 9.
4. **Movimento de estoque entre 00:00 e 03:00 UTC** — cai no dia anterior em Brasília. Teste em Task 5.
5. **Mês sem nenhum dado** — nenhum relatório pode lançar nem exibir `NaN`/divisão por zero. Teste em Tasks 6, 7, 8 e 9.
6. **Empresa sem dados lendo relatório enquanto outra tem dados** — controle positivo obrigatório. Teste em Task 10.

---

## File Structure

**Fase 0 — domínio do fiado**

| Arquivo | Responsabilidade |
|---|---|
| `src/RevendaApp.Domain/Entities/Venda.cs` (modify) | `ValorPagoFiado`, `SaldoFiado`, `AbaterFiado` |
| `src/RevendaApp.Infrastructure/Configurations/VendaConfiguration.cs` (modify) | precisão da coluna + `Ignore(SaldoFiado)` |
| `src/RevendaApp.Infrastructure/Migrations/*_ValorPagoFiadoNaVenda.cs` (create) | coluna + backfill |
| `src/RevendaApp.Web/Common/VendaLucro.cs` (create) | regra única de lucro reconhecido |
| `src/RevendaApp.Web/Features/Clientes/Pagar/RegistrarPagamentoCommandHandler.cs` (modify) | alocação FIFO parcial |
| `src/RevendaApp.Web/Features/Admin/Vendas/ReversaoVendaHelper.cs` (modify) | estorna `SaldoFiado` |
| `src/RevendaApp.Web/Features/Admin/Vendas/{Editar,Excluir}VendaCommandHandler.cs` (modify) | guarda por venda |
| `src/RevendaApp.Web/Features/Home/ObterDashboardQueryHandler.cs` (modify) | usa `VendaLucro` |

**Fase 1 — normalização do legado**

| Arquivo | Responsabilidade |
|---|---|
| `src/RevendaApp.Web/Features/Fiado/Normalizacao/NormalizarFiadoLegado.cs` (create) | replay FIFO + marcador + reconciliação |
| `src/RevendaApp.Domain/Entities/RegistroAuditoria.cs` (modify) | `Acoes.FiadoLegadoNormalizado`, `Origens.Sistema` |
| `src/RevendaApp.Web/Program.cs` (modify) | chamada no bootstrap + logs |

**Fase 2 — área de relatórios + estoque**

| Arquivo | Responsabilidade |
|---|---|
| `src/RevendaApp.Web/Common/Relatorios/PeriodoMes.cs` (create) | mês civil de Brasília, intervalo UTC |
| `src/RevendaApp.Web/Features/Relatorios/Shell/RelatorioShell.razor` (create) | título + seletor de mês + estados |
| `src/RevendaApp.Web/Features/Relatorios/Home/RelatoriosHome.razor` (create) | landing com cartões |
| `src/RevendaApp.Web/Features/Relatorios/Estoque/*` (create) | query + handler + página |
| `src/RevendaApp.Web/Components/Layout/MainLayout.razor` (modify) | botão "Relatórios" |
| `src/RevendaApp.Web/Features/Estoque/Listar/EstoqueLista.razor` (modify) | botão "Relatório do mês" |
| `src/RevendaApp.Infrastructure/Configurations/*` (modify) | índices compostos |

**Fases 3–5 — vendas, fiado, vasilhames**

| Arquivo | Responsabilidade |
|---|---|
| `src/RevendaApp.Web/Features/Relatorios/Vendas/*` (create) | relatório de vendas do mês |
| `src/RevendaApp.Web/Features/Relatorios/Fiado/*` (create) | aging + recebimentos |
| `src/RevendaApp.Web/Features/Relatorios/Vasilhames/*` (create) | em aberto + movimentos do mês |
| `tests/RevendaApp.IntegrationTests/RelatoriosIsolationTests.cs` (create) | isolamento entre empresas |

---

# Fase 0 — quitação parcial de fiado

### Task 1: `Venda` registra quanto já foi pago do fiado

**Files:**
- Modify: `src/RevendaApp.Domain/Entities/Venda.cs:44-58` (após `ExcluidaEm`), `:92-98` (`MarcarFiadoQuitado`)
- Test: `tests/RevendaApp.UnitTests/Domain/VendaFiadoTests.cs` (create)

**Interfaces:**
- Consumes: nada.
- Produces: `Venda.ValorPagoFiado : decimal`, `Venda.SaldoFiado : decimal`, `Venda.AbaterFiado(decimal valor) : decimal`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/RevendaApp.UnitTests/Domain/VendaFiadoTests.cs
using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;

namespace RevendaApp.UnitTests.Domain;

public class VendaFiadoTests
{
    private static Venda VendaFiado(decimal valor = 100m, DateTime? dataHora = null)
    {
        var venda = new Venda(Guid.NewGuid(), FormaPagamento.Fiado, dataHora);
        venda.AdicionarItem(Guid.NewGuid(), 1, valor, 60m);
        return venda;
    }

    [Fact]
    public void AbaterFiado_aloca_parcialmente_e_mantem_venda_aberta()
    {
        var venda = VendaFiado(100m);

        var alocado = venda.AbaterFiado(40m);

        Assert.Equal(40m, alocado);
        Assert.Equal(40m, venda.ValorPagoFiado);
        Assert.Equal(60m, venda.SaldoFiado);
        Assert.Equal(VendaStatus.FiadoAberto, venda.Status);
    }

    [Fact]
    public void AbaterFiado_quita_quando_alocacao_cobre_o_total()
    {
        var venda = VendaFiado(100m);

        venda.AbaterFiado(40m);
        var alocado = venda.AbaterFiado(60m);

        Assert.Equal(60m, alocado);
        Assert.Equal(0m, venda.SaldoFiado);
        Assert.Equal(VendaStatus.FiadoQuitado, venda.Status);
    }

    [Fact]
    public void AbaterFiado_nao_aloca_alem_do_saldo_e_devolve_o_excedente()
    {
        var venda = VendaFiado(100m);

        var alocado = venda.AbaterFiado(150m);

        Assert.Equal(100m, alocado);
        Assert.Equal(100m, venda.ValorPagoFiado);
        Assert.Equal(0m, venda.SaldoFiado);
    }

    [Fact]
    public void AbaterFiado_ignora_venda_que_nao_e_fiado()
    {
        var venda = new Venda(Guid.NewGuid(), FormaPagamento.Pix);
        venda.AdicionarItem(Guid.NewGuid(), 1, 100m, 60m);

        var alocado = venda.AbaterFiado(50m);

        Assert.Equal(0m, alocado);
        Assert.Equal(0m, venda.ValorPagoFiado);
    }

    [Fact]
    public void AbaterFiado_ignora_venda_ja_quitada()
    {
        var venda = VendaFiado(100m);
        venda.AbaterFiado(100m);

        var alocado = venda.AbaterFiado(10m);

        Assert.Equal(0m, alocado);
        Assert.Equal(100m, venda.ValorPagoFiado);
    }

    [Fact]
    public void AbaterFiado_nao_altera_venda_excluida()
    {
        var venda = VendaFiado(100m);
        venda.Excluir();

        var alocado = venda.AbaterFiado(50m);

        Assert.Equal(0m, alocado);
        Assert.Equal(0m, venda.ValorPagoFiado);
    }

    [Fact]
    public void AbaterFiado_ignora_valor_nao_positivo()
    {
        var venda = VendaFiado(100m);

        Assert.Equal(0m, venda.AbaterFiado(0m));
        Assert.Equal(0m, venda.AbaterFiado(-10m));
        Assert.Equal(0m, venda.ValorPagoFiado);
    }

    [Fact]
    public void MarcarFiadoQuitado_equivale_a_abater_o_saldo_restante()
    {
        var venda = VendaFiado(100m);
        venda.AbaterFiado(30m);

        venda.MarcarFiadoQuitado();

        Assert.Equal(100m, venda.ValorPagoFiado);
        Assert.Equal(0m, venda.SaldoFiado);
        Assert.Equal(VendaStatus.FiadoQuitado, venda.Status);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/RevendaApp.UnitTests --filter FullyQualifiedName~VendaFiadoTests`
Expected: FAIL — `Venda` não contém `AbaterFiado`/`ValorPagoFiado`/`SaldoFiado` (CS1061).

- [ ] **Step 3: Implement the domain members in `src/RevendaApp.Domain/Entities/Venda.cs`**

Adicione a propriedade logo após `ExcluidaEm` (linha 44) e substitua `MarcarFiadoQuitado` (linha 92):

```csharp
/// <summary>
/// Quanto do valor desta venda fiado já foi pago. Alocado por pagamento (parcial ou total),
/// em ordem FIFO. Zero em vendas que não são fiado e em fiado legado anterior à coluna.
/// </summary>
public decimal ValorPagoFiado { get; private set; }

/// <summary>Quanto ainda falta pagar desta venda fiado.</summary>
public decimal SaldoFiado => ValorTotal - ValorPagoFiado;

/// <summary>
/// Aloca um pagamento nesta venda fiado em aberto e quita quando a alocação cobre o total.
/// Devolve o valor efetivamente alocado (nunca maior que <see cref="SaldoFiado"/>).
/// </summary>
public decimal AbaterFiado(decimal valor)
{
    if (valor <= 0 || ExcluidaEm is not null)
    {
        return 0m;
    }

    if (FormaPagamento != FormaPagamento.Fiado || Status != VendaStatus.FiadoAberto)
    {
        return 0m;
    }

    var alocado = Math.Min(valor, SaldoFiado);
    if (alocado <= 0m)
    {
        return 0m;
    }

    ValorPagoFiado += alocado;
    if (SaldoFiado == 0m)
    {
        Status = VendaStatus.FiadoQuitado;
    }

    return alocado;
}
```

E `MarcarFiadoQuitado` passa a delegar (mantém a assinatura pública usada pelo seed):

```csharp
public void MarcarFiadoQuitado()
{
    if (FormaPagamento == FormaPagamento.Fiado && Status == VendaStatus.FiadoAberto)
    {
        ValorPagoFiado = ValorTotal;
        Status = VendaStatus.FiadoQuitado;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RevendaApp.UnitTests --filter FullyQualifiedName~VendaFiadoTests`
Expected: PASS (8 testes).

- [ ] **Step 5: Commit**

```bash
git add src/RevendaApp.Domain/Entities/Venda.cs tests/RevendaApp.UnitTests/Domain/VendaFiadoTests.cs
git commit -m "feat: registra quitacao parcial de fiado por venda"
```

---

### Task 2: Pagamento aloca em FIFO parcial

**Files:**
- Modify: `src/RevendaApp.Web/Features/Clientes/Pagar/RegistrarPagamentoCommandHandler.cs:56-70`
- Modify: `tests/RevendaApp.UnitTests/Features/Clientes/RegistrarPagamentoCommandHandlerTests.cs` (o teste `Handle_NaoDeveMarcarVendaQuitada_QuandoPagamentoForParcial` muda de contrato)

**Interfaces:**
- Consumes: `Venda.AbaterFiado(decimal) : decimal` (Task 1).
- Produces: nenhuma API nova. Contrato observável: pagamento parcial grava `ValorPagoFiado`.

- [ ] **Step 1: Update the partial-payment test and add the new ones**

Substitua `Handle_NaoDeveMarcarVendaQuitada_QuandoPagamentoForParcial` por:

```csharp
[Fact]
public async Task Handle_PagamentoParcial_DeveAlocarNaVendaSemQuitar()
{
    var (db, cliente) = await CriarClienteAsync(saldoDevedor: 100m);
    var venda = await RegistrarVendaFiadoAsync(db, cliente.Id, valorItem: 100m);
    var handler = new RegistrarPagamentoCommandHandler(db, TenantTeste.Contexto(), new VendasAtualizadasNotifier());

    await handler.Handle(new RegistrarPagamentoCommand(cliente.Id, 40m, FormaPagamento.Dinheiro), CancellationToken.None);

    var vendaAtualizada = await db.Vendas.FindAsync(venda.Id);
    Assert.Equal(40m, vendaAtualizada!.ValorPagoFiado);
    Assert.Equal(60m, vendaAtualizada.SaldoFiado);
    Assert.Equal(VendaStatus.FiadoAberto, vendaAtualizada.Status);
    Assert.Equal(60m, (await db.Clientes.FindAsync(cliente.Id))!.SaldoDevedor);
}

[Fact]
public async Task Handle_PagamentoQueCobreDuasVendas_DeveQuitarAAntigaEAbaterASeguinte()
{
    var (db, cliente) = await CriarClienteAsync(saldoDevedor: 100m);
    var vendaAntiga = await RegistrarVendaFiadoAsync(db, cliente.Id, valorItem: 60m, dataHora: new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc));
    var vendaNova = await RegistrarVendaFiadoAsync(db, cliente.Id, valorItem: 40m, dataHora: new DateTime(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc));
    var handler = new RegistrarPagamentoCommandHandler(db, TenantTeste.Contexto(), new VendasAtualizadasNotifier());

    await handler.Handle(new RegistrarPagamentoCommand(cliente.Id, 70m, FormaPagamento.Dinheiro), CancellationToken.None);

    var antiga = await db.Vendas.FindAsync(vendaAntiga.Id);
    var nova = await db.Vendas.FindAsync(vendaNova.Id);
    Assert.Equal(VendaStatus.FiadoQuitado, antiga!.Status);
    Assert.Equal(10m, nova!.ValorPagoFiado);
    Assert.Equal(30m, nova.SaldoFiado);
    Assert.Equal(VendaStatus.FiadoAberto, nova.Status);
    Assert.Equal(30m, (await db.Clientes.FindAsync(cliente.Id))!.SaldoDevedor);
}

[Fact]
public async Task Handle_PagamentoMaiorQueADivida_NaoDeveAlocarOExcedente()
{
    var (db, cliente) = await CriarClienteAsync(saldoDevedor: 100m);
    var venda = await RegistrarVendaFiadoAsync(db, cliente.Id, valorItem: 100m);
    var handler = new RegistrarPagamentoCommandHandler(db, TenantTeste.Contexto(), new VendasAtualizadasNotifier());

    await handler.Handle(new RegistrarPagamentoCommand(cliente.Id, 150m, FormaPagamento.Dinheiro), CancellationToken.None);

    var vendaAtualizada = await db.Vendas.FindAsync(venda.Id);
    Assert.Equal(100m, vendaAtualizada!.ValorPagoFiado);
    Assert.Equal(0m, (await db.Clientes.FindAsync(cliente.Id))!.SaldoDevedor);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RevendaApp.UnitTests --filter FullyQualifiedName~RegistrarPagamentoCommandHandlerTests`
Expected: FAIL — `ValorPagoFiado` continua 0 no pagamento parcial.

- [ ] **Step 3: Replace the FIFO loop**

Em `RegistrarPagamentoCommandHandler.cs`, o trecho do `foreach` (linhas ~56-70) passa a ser:

```csharp
var valorPago = command.Valor;
foreach (var venda in vendasFiado)
{
    if (valorPago <= 0)
    {
        break;
    }

    // FIFO com alocação parcial: cada venda absorve o que o pagamento cobre do seu saldo.
    // O excedente segue para a próxima venda; o que sobrar ao fim não é alocado a ninguém
    // (crédito a favor do cliente não é modelado nesta entrega).
    valorPago -= venda.AbaterFiado(valorPago);
}
```

- [ ] **Step 4: Run the whole payment test class**

Run: `dotnet test tests/RevendaApp.UnitTests --filter FullyQualifiedName~RegistrarPagamentoCommandHandlerTests`
Expected: PASS (todos, incluindo os de FIFO que já existiam).

- [ ] **Step 5: Commit**

```bash
git add src/RevendaApp.Web/Features/Clientes/Pagar/RegistrarPagamentoCommandHandler.cs tests/RevendaApp.UnitTests/Features/Clientes/RegistrarPagamentoCommandHandlerTests.cs
git commit -m "feat: aloca pagamento fiado em fifo parcial por venda"
```

---

### Task 3: Estorno e guardas de edição/exclusão usam o saldo da venda

**Files:**
- Modify: `src/RevendaApp.Web/Features/Admin/Vendas/ReversaoVendaHelper.cs:96-103`
- Modify: `src/RevendaApp.Web/Features/Admin/Vendas/EditarVendaCommandHandler.cs:56-63`
- Modify: `src/RevendaApp.Web/Features/Admin/Vendas/ExcluirVendaCommandHandler.cs:45-52`
- Test: `tests/RevendaApp.UnitTests/Features/Admin/Vendas/ReversaoVendaFiadoTests.cs` (create), `tests/RevendaApp.UnitTests/Features/Admin/Vendas/EditarVendaCommandHandlerTests.cs` (modify)

**Interfaces:**
- Consumes: `Venda.SaldoFiado`, `Venda.Status`, `Venda.AbaterFiado` (Task 1).
- Produces: nenhuma API nova.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/RevendaApp.UnitTests/Features/Admin/Vendas/ReversaoVendaFiadoTests.cs
using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Features.Admin.Vendas;

namespace RevendaApp.UnitTests.Features.Admin.Vendas;

public class ReversaoVendaFiadoTests
{
    [Fact]
    public async Task Reversao_de_venda_parcialmente_paga_abate_apenas_o_saldo_fiado()
    {
        await using var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria", "11988887777", "Rua A, 1");
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        var venda = new Venda(cliente.Id, FormaPagamento.Fiado);
        venda.AdicionarItem(Guid.NewGuid(), 1, 100m, 60m);
        db.Vendas.Add(venda);
        cliente.AdicionarSaldoDevedor(100m);
        venda.AbaterFiado(40m);
        cliente.RegistrarPagamento(40m);
        await db.SaveChangesAsync();

        await ReversaoVendaHelper.ReverterAsync(db, venda, CancellationToken.None);
        await db.SaveChangesAsync();

        // Estorna só o que ainda estava em aberto (60), não os 100 do total.
        Assert.Equal(0m, (await db.Clientes.FindAsync(cliente.Id))!.SaldoDevedor);
    }
}
```

Em `EditarVendaCommandHandlerTests.cs`, substitua o teste que dependia de `SaldoDevedor < ValorTotal`
por dois (use o helper de montagem que a classe já tem):

```csharp
[Fact]
public async Task Edicao_de_venda_fiado_aberta_parcialmente_paga_e_permitida()
{
    // venda fiado 100, paga 40, ainda FiadoAberto
    var resultado = await handler.Handle(comandoEdicao, CancellationToken.None);

    Assert.True(resultado.Sucesso);
}

[Fact]
public async Task Edicao_de_venda_fiado_quitada_e_bloqueada()
{
    // venda fiado 100, paga 100 → FiadoQuitado
    var excecao = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        handler.Handle(comandoEdicao, CancellationToken.None).AsTask());

    Assert.Contains("já foi quitada", excecao.Message);
}
```

O mesmo par de asserções vale para exclusão, em `ExcluirVendaCommandHandlerTests` (crie o arquivo
se não existir, com o mesmo helper).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RevendaApp.UnitTests --filter "FullyQualifiedName~ReversaoVendaFiadoTests|FullyQualifiedName~EditarVendaCommandHandlerTests"`
Expected: FAIL — estorno devolve 100 (saldo vai a −40→0 por clamp) e a guarda antiga ainda compara com `SaldoDevedor`.

- [ ] **Step 3: Fix the reversal and both guards**

`ReversaoVendaHelper.cs`, no bloco `if (venda.FormaPagamento == FormaPagamento.Fiado)` (linha ~96), troque `cliente.RegistrarPagamento(venda.ValorTotal)` por:

```csharp
// Estorna apenas o que a venda ainda devia: se houve pagamento parcial já alocado,
// devolver o ValorTotal inteiro criaria crédito que o cliente nunca pagou.
cliente.RegistrarPagamento(venda.SaldoFiado);
```

Em `EditarVendaCommandHandler.cs:56-63` e `ExcluirVendaCommandHandler.cs:45-52`, a guarda passa a ser:

```csharp
// Venda fiado já quitada teve o lucro reconhecido; desfazê-la exigiria estornar o pagamento,
// o que o fluxo não faz. Fiado aberto — mesmo parcialmente pago — pode ser revertido.
if (venda.FormaPagamento == FormaPagamento.Fiado && venda.Status == VendaStatus.FiadoQuitado)
{
    throw new InvalidOperationException(
        "Não é possível editar: esta venda fiado já foi quitada.");
}
```

(Ajuste o verbo da mensagem para "excluir" no handler de exclusão.) A variável `cliente` deixa de ser usada nessas guardas — remova a linha `var cliente = venda.Cliente!;` se ficar sem uso.

- [ ] **Step 4: Run the affected tests**

Run: `dotnet test tests/RevendaApp.UnitTests --filter "FullyQualifiedName~Admin.Vendas"`
Expected: PASS. Se algum teste antigo assumia o bloqueio por saldo, ele deve ser reescrito para o novo contrato (venda quitada bloqueia, aberta permite).

- [ ] **Step 5: Commit**

```bash
git add src/RevendaApp.Web/Features/Admin/Vendas/ReversaoVendaHelper.cs src/RevendaApp.Web/Features/Admin/Vendas/EditarVendaCommandHandler.cs src/RevendaApp.Web/Features/Admin/Vendas/ExcluirVendaCommandHandler.cs tests/RevendaApp.UnitTests/Features/Admin/Vendas/
git commit -m "fix: estorna fiado pelo saldo em aberto e ajusta guardas de venda"
```

---

### Task 4: Lucro reconhecido proporcional ao fiado pago

**Files:**
- Create: `src/RevendaApp.Web/Common/VendaLucro.cs`
- Modify: `src/RevendaApp.Web/Features/Home/ObterDashboardQueryHandler.cs:42-45,227-230`
- Test: `tests/RevendaApp.UnitTests/Common/VendaLucroTests.cs` (create), `tests/RevendaApp.UnitTests/Features/Home/ObterDashboardQueryHandlerTests.cs` (modify)

**Interfaces:**
- Consumes: `Venda.ValorPagoFiado`, `Venda.ValorTotal`, `Venda.LucroTotal`, `Venda.Status`.
- Produces: `VendaLucro.Reconhecido(Venda venda) : decimal`, `VendaLucro.Reconhecido(IEnumerable<Venda> vendas) : decimal`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/RevendaApp.UnitTests/Common/VendaLucroTests.cs
using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.Web.Common;

namespace RevendaApp.UnitTests.Common;

public class VendaLucroTests
{
    private static Venda Venda(FormaPagamento forma, decimal preco = 100m, decimal custo = 60m)
    {
        var venda = new Venda(Guid.NewGuid(), forma);
        venda.AdicionarItem(Guid.NewGuid(), 1, preco, custo);
        return venda;
    }

    [Fact]
    public void Venda_paga_no_ato_reconhece_lucro_integral()
    {
        Assert.Equal(40m, VendaLucro.Reconhecido(Venda(FormaPagamento.Pix)));
    }

    [Fact]
    public void Fiado_em_aberto_nao_reconhece_lucro()
    {
        Assert.Equal(0m, VendaLucro.Reconhecido(Venda(FormaPagamento.Fiado)));
    }

    [Fact]
    public void Fiado_pago_pela_metade_reconhece_metade_do_lucro()
    {
        var venda = Venda(FormaPagamento.Fiado);
        venda.AbaterFiado(50m);

        Assert.Equal(20m, VendaLucro.Reconhecido(venda));
    }

    [Fact]
    public void Fiado_quitado_reconhece_lucro_integral()
    {
        var venda = Venda(FormaPagamento.Fiado);
        venda.AbaterFiado(100m);

        Assert.Equal(40m, VendaLucro.Reconhecido(venda));
    }

    [Fact]
    public void Soma_reconhecido_de_varias_vendas()
    {
        var pix = Venda(FormaPagamento.Pix);
        var fiado = Venda(FormaPagamento.Fiado);
        fiado.AbaterFiado(50m);

        Assert.Equal(60m, VendaLucro.Reconhecido([pix, fiado]));
    }
}
```

Em `ObterDashboardQueryHandlerTests`, adicione o caso novo:

```csharp
[Fact]
public async Task Dashboard_reconhece_lucro_proporcional_de_fiado_parcialmente_pago()
{
    // venda fiado 100, custo 60 (lucro 40), paga 50 → reconhece 20
    // asserção: result.LucroTotal == 20m
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RevendaApp.UnitTests --filter "FullyQualifiedName~VendaLucroTests|FullyQualifiedName~ObterDashboardQueryHandlerTests"`
Expected: FAIL — `VendaLucro` não existe (CS0246).

- [ ] **Step 3: Create `VendaLucro` and use it in the dashboard**

```csharp
// src/RevendaApp.Web/Common/VendaLucro.cs
using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;

namespace RevendaApp.Web.Common;

/// <summary>
/// Regra única de lucro reconhecido: venda paga no ato reconhece tudo; fiado reconhece na
/// proporção do que já foi pago (e integral quando quitado). Usada pelo dashboard e pelos
/// relatórios para não existirem duas verdades.
/// </summary>
public static class VendaLucro
{
    public static decimal Reconhecido(Venda venda)
    {
        if (venda.FormaPagamento != FormaPagamento.Fiado)
        {
            return venda.LucroTotal;
        }

        if (venda.ValorTotal <= 0m)
        {
            return 0m;
        }

        return venda.LucroTotal * (venda.ValorPagoFiado / venda.ValorTotal);
    }

    public static decimal Reconhecido(IEnumerable<Venda> vendas) => vendas.Sum(Reconhecido);
}
```

No `ObterDashboardQueryHandler.cs`, o lambda local `lucroReconhecido` (linhas 42-45) vira
`vendas.Sum(VendaLucro.Reconhecido)`, e o `static decimal LucroReconhecido(IEnumerable<Venda> vs)`
(linha 227) vira `VendaLucro.Reconhecido(vs)`. Remova os comentários que explicam a regra antiga e
aponte para `VendaLucro`.

- [ ] **Step 4: Run dashboard and VendaLucro tests**

Run: `dotnet test tests/RevendaApp.UnitTests --filter "FullyQualifiedName~VendaLucroTests|FullyQualifiedName~ObterDashboardQueryHandlerTests"`
Expected: PASS.

- [ ] **Step 5: Run the whole unit suite**

Run: `dotnet test tests/RevendaApp.UnitTests`
Expected: PASS. Corrija qualquer teste que assumia lucro zero para fiado parcialmente pago.

- [ ] **Step 6: Commit**

```bash
git add src/RevendaApp.Web/Common/VendaLucro.cs src/RevendaApp.Web/Features/Home/ObterDashboardQueryHandler.cs tests/RevendaApp.UnitTests/Common/VendaLucroTests.cs tests/RevendaApp.UnitTests/Features/Home/ObterDashboardQueryHandlerTests.cs
git commit -m "feat: reconhece lucro de fiado na proporcao do valor pago"
```

---

### Task 5: Migração `ValorPagoFiadoNaVenda` com backfill

> **Nota de ordem:** esta task usa o mesmo `VendaConfiguration` que a Task 11 altera. Se a Task 11
> já tiver rodado (branch retomada fora de ordem), gere esta migração **antes** da de índices, ou
> regenere-a para não arrastar os índices junto.

**Files:**
- Modify: `src/RevendaApp.Infrastructure/Configurations/VendaConfiguration.cs`
- Create: `src/RevendaApp.Infrastructure/Migrations/<timestamp>_ValorPagoFiadoNaVenda.cs` (gerada)
- Test: `tests/RevendaApp.IntegrationTests/BackfillFiadoTests.cs` (create)

**Interfaces:**
- Consumes: coluna `Vendas.ValorPagoFiado` (Task 1 mapeada).
- Produces: tabela `Vendas` com `ValorPagoFiado numeric(18,2) NOT NULL DEFAULT 0`, backfill aplicado.

- [ ] **Step 1: Map the column**

Em `VendaConfiguration.cs`, junto de `Desconto`/`ValorEntrega`:

```csharp
builder.Property(v => v.ValorPagoFiado).HasPrecision(18, 2);
builder.Ignore(v => v.SaldoFiado);
```

- [ ] **Step 2: Generate the migration**

```bash
dotnet-ef migrations add ValorPagoFiadoNaVenda --project src/RevendaApp.Infrastructure --startup-project src/RevendaApp.Infrastructure -o Migrations
```

- [ ] **Step 3: Write the backfill into the generated `Up`**

Depois do `AddColumn`, antes do fim do `Up`:

```csharp
// Vendas fiado quitadas antes desta coluna foram pagas integralmente: o valor é exato.
// ValorTotal não é coluna — soma os itens e desconta o desconto, somando a entrega.
migrationBuilder.Sql("""
    UPDATE "Vendas" v
    SET "ValorPagoFiado" = itens."ValorBruto" - v."Desconto" + v."ValorEntrega"
    FROM (
        SELECT "VendaId", SUM("PrecoUnitario" * "Quantidade") AS "ValorBruto"
        FROM "ItensVenda"
        GROUP BY "VendaId"
    ) itens
    WHERE itens."VendaId" = v."Id"
      AND v."FormaPagamento" = 'Fiado'
      AND v."Status" = 'FiadoQuitado';
    """);
```

O `Down` apenas remove a coluna (o backfill não é reversível e não precisa ser).

- [ ] **Step 4: Write the failing integration test**

```csharp
// tests/RevendaApp.IntegrationTests/BackfillFiadoTests.cs
// classe com IClassFixture<DatabaseFixture>, IAsyncLifetime, ResetDatabaseAsync no InitializeAsync
[Fact]
public async Task Backfill_marca_vendas_fiado_quitadas_com_valor_total()
{
    // Cria cliente + produto + venda fiado 100 (2 itens de 50) + desconto 10 + entrega 5,
    // chama venda.MarcarFiadoQuitado() e salva. Depois roda o SQL do backfill
    // (db.Database.ExecuteSqlRaw com o mesmo comando da migração) e verifica
    // ValorPagoFiado == 95 (100 - 10 + 5).
}

[Fact]
public async Task Backfill_nao_altera_venda_fiado_aberta()
{
    // venda fiado aberta continua com ValorPagoFiado == 0.
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/RevendaApp.IntegrationTests --filter FullyQualifiedName~BackfillFiadoTests`
Expected: PASS.

- [ ] **Step 6: Verify the app boots with the migration**

Run: `timeout 180s dotnet build`
Expected: build limpo.

- [ ] **Step 7: Commit**

```bash
git add src/RevendaApp.Infrastructure/Configurations/VendaConfiguration.cs src/RevendaApp.Infrastructure/Migrations/ tests/RevendaApp.IntegrationTests/BackfillFiadoTests.cs
git commit -m "feat: adiciona coluna de valor pago fiado com backfill"
```

---

# Fase 1 — normalização do fiado legado

### Task 6: `NormalizarFiadoLegado` reconstrói a alocação antiga

**Files:**
- Create: `src/RevendaApp.Web/Features/Fiado/Normalizacao/NormalizarFiadoLegado.cs`
- Modify: `src/RevendaApp.Domain/Entities/RegistroAuditoria.cs` (`Acoes`, `Origens`)
- Test: `tests/RevendaApp.UnitTests/Features/Fiado/NormalizarFiadoLegadoTests.cs` (create)

**Interfaces:**
- Consumes: `Venda.DefinirValorPagoFiado` (novo, criado nesta task), `Venda.SaldoFiado`, `RegistroAuditoria`.
- Produces:
  - `Venda.DefinirValorPagoFiado(decimal valor) : void` (idempotente por construção)
  - `NormalizarFiadoLegado.ExecutarAsync(AppDbContext db, CancellationToken ct) : Task<ResultadoNormalizacao>`
  - `ResultadoNormalizacao(int ClientesAnalisados, int VendasNormalizadas, int Divergencias)` (record)
  - `RegistroAuditoria.Acoes.FiadoLegadoNormalizado`, `RegistroAuditoria.Origens.Sistema`

**Por que `DefinirValorPagoFiado` e não `AbaterFiado`:** a normalização é um *replay* do histórico,
não uma alocação incremental. Se usasse `AbaterFiado`, rodar duas vezes somaria de novo e
corromperia o dado. `DefinirValorPagoFiado` calcula o alvo e **sobrescreve** o estado, então a
normalização é idempotente por construção — sem backdoor de teste nem flag.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/RevendaApp.UnitTests/Features/Fiado/NormalizarFiadoLegadoTests.cs
using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Features.Fiado.Normalizacao;

namespace RevendaApp.UnitTests.Features.Fiado;

public class NormalizarFiadoLegadoTests
{
    private static async Task<(Infrastructure.AppDbContext Db, Cliente Cliente)> CenarioAsync(
        decimal[] valoresFiado, decimal[] pagamentos)
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var cliente = new Cliente("Maria", "11988887777", "Rua A, 1");
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        var dia = 1;
        foreach (var valor in valoresFiado)
        {
            var venda = new Venda(cliente.Id, FormaPagamento.Fiado, new DateTime(2026, 1, dia++, 10, 0, 0, DateTimeKind.Utc));
            venda.AdicionarItem(Guid.NewGuid(), 1, valor, valor / 2);
            db.Vendas.Add(venda);
            cliente.AdicionarSaldoDevedor(valor);
        }

        var diaPagamento = 1;
        foreach (var pagamento in pagamentos)
        {
            // VendaId null = abatimento de saldo devedor (fiado).
            db.Pagamentos.Add(new Pagamento(cliente.Id, pagamento, FormaPagamento.Dinheiro,
                data: new DateTime(2026, 2, diaPagamento++, 10, 0, 0, DateTimeKind.Utc)));
            cliente.RegistrarPagamento(pagamento);
        }

        await db.SaveChangesAsync();
        return (db, cliente);
    }

    [Fact]
    public async Task Normalizacao_aloca_pagamentos_parciais_nas_vendas_mais_antigas()
    {
        var (db, _) = await CenarioAsync([60m, 40m], [50m]);

        await NormalizarFiadoLegado.ExecutarAsync(db, CancellationToken.None);

        var vendas = db.Vendas.OrderBy(v => v.DataHora).ToList();
        Assert.Equal(50m, vendas[0].ValorPagoFiado);
        Assert.Equal(10m, vendas[0].SaldoFiado);
        Assert.Equal(0m, vendas[1].ValorPagoFiado);
    }

    [Fact]
    public async Task Normalizacao_quita_venda_quando_a_alocacao_cobre_o_total()
    {
        var (db, _) = await CenarioAsync([60m, 40m], [70m]);

        await NormalizarFiadoLegado.ExecutarAsync(db, CancellationToken.None);

        var vendas = db.Vendas.OrderBy(v => v.DataHora).ToList();
        Assert.Equal(VendaStatus.FiadoQuitado, vendas[0].Status);
        Assert.Equal(10m, vendas[1].ValorPagoFiado);
        Assert.Equal(VendaStatus.FiadoAberto, vendas[1].Status);
    }

    [Fact]
    public async Task Normalizacao_ignora_pagamento_de_venda_no_ato()
    {
        var (db, cliente) = await CenarioAsync([100m], []);
        // Pagamento vinculado a venda (não-fiado) não pode abater fiado.
        db.Pagamentos.Add(new Pagamento(cliente.Id, 100m, FormaPagamento.Pix, vendaId: Guid.NewGuid()));
        await db.SaveChangesAsync();

        await NormalizarFiadoLegado.ExecutarAsync(db, CancellationToken.None);

        Assert.Equal(0m, db.Vendas.Single().ValorPagoFiado);
    }

    [Fact]
    public async Task Normalizacao_ignora_venda_excluida()
    {
        var (db, _) = await CenarioAsync([100m], [100m]);
        db.Vendas.Single().Excluir();
        await db.SaveChangesAsync();

        await NormalizarFiadoLegado.ExecutarAsync(db, CancellationToken.None);

        Assert.Equal(0m, db.Vendas.Single().ValorPagoFiado);
    }

    [Fact]
    public async Task Normalizacao_e_idempotente()
    {
        var (db, _) = await CenarioAsync([60m, 40m], [70m]);

        await NormalizarFiadoLegado.ExecutarAsync(db, CancellationToken.None);
        var primeira = db.Vendas.OrderBy(v => v.DataHora).Select(v => v.ValorPagoFiado).ToList();

        // Remove o marcador e roda de novo: o estado tem de ser o mesmo (set absoluto, não acúmulo).
        var marcador = db.RegistrosAuditoria.Single(r => r.Acao == RegistroAuditoria.Acoes.FiadoLegadoNormalizado);
        db.RegistrosAuditoria.Remove(marcador);
        await db.SaveChangesAsync();

        await NormalizarFiadoLegado.ExecutarAsync(db, CancellationToken.None);
        var segunda = db.Vendas.OrderBy(v => v.DataHora).Select(v => v.ValorPagoFiado).ToList();

        Assert.Equal(primeira, segunda);
    }

    [Fact]
    public async Task Normalizacao_nao_aloca_excedente_de_credito()
    {
        var (db, _) = await CenarioAsync([100m], [150m]);

        await NormalizarFiadoLegado.ExecutarAsync(db, CancellationToken.None);

        Assert.Equal(100m, db.Vendas.Single().ValorPagoFiado);
    }

    [Fact]
    public async Task Normalizacao_nao_roda_quando_o_marcador_ja_existe()
    {
        var (db, _) = await CenarioAsync([100m], [100m]);
        // Marcador já gravado (como numa subida anterior) e venda ainda sem alocação:
        // a segunda execução não pode tocar nada.
        db.RegistrosAuditoria.Add(new RegistroAuditoria(null, null, null,
            RegistroAuditoria.Acoes.FiadoLegadoNormalizado, "resumo", RegistroAuditoria.Origens.Sistema));
        await db.SaveChangesAsync();

        var resultado = await NormalizarFiadoLegado.ExecutarAsync(db, CancellationToken.None);

        Assert.Equal(0, resultado.VendasNormalizadas);
        Assert.Equal(0m, db.Vendas.Single().ValorPagoFiado);
        Assert.Equal(1, db.RegistrosAuditoria.Count());
    }

    [Fact]
    public async Task Normalizacao_registra_divergencia_sem_alterar_saldo_do_cliente()
    {
        var (db, cliente) = await CenarioAsync([100m], [40m]);
        // Simula ajuste manual: saldo do cadastro difere da soma das vendas.
        cliente.RegistrarPagamento(50m);
        await db.SaveChangesAsync();

        var resultado = await NormalizarFiadoLegado.ExecutarAsync(db, CancellationToken.None);

        Assert.Equal(1, resultado.Divergencias);
        Assert.Equal(10m, (await db.Clientes.FindAsync(cliente.Id))!.SaldoDevedor);
        var marcador = db.RegistrosAuditoria.Single(r => r.Acao == RegistroAuditoria.Acoes.FiadoLegadoNormalizado);
        Assert.Contains(cliente.Id.ToString(), marcador.Detalhes);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RevendaApp.UnitTests --filter FullyQualifiedName~NormalizarFiadoLegadoTests`
Expected: FAIL — `NormalizarFiadoLegado` não existe.

- [ ] **Step 3: Add `DefinirValorPagoFiado` to `Venda` and the audit constants**

Em `src/RevendaApp.Domain/Entities/Venda.cs`, logo abaixo de `AbaterFiado`:

```csharp
/// <summary>
/// Define o total já pago desta venda fiado. Usado pela normalização do legado, que replica o
/// histórico de pagamentos: é um <b>set</b> absoluto, não um acréscimo, para a operação ser
/// idempotente. Quita a venda quando o valor cobre o total.
/// </summary>
internal void DefinirValorPagoFiado(decimal valor)
{
    if (FormaPagamento != FormaPagamento.Fiado)
    {
        return;
    }

    ValorPagoFiado = Math.Clamp(valor, 0m, ValorTotal);

    if (SaldoFiado == 0m && Status == VendaStatus.FiadoAberto)
    {
        Status = VendaStatus.FiadoQuitado;
    }
}
```

`public` (não `internal`): `Venda` vive em `RevendaApp.Domain` e a normalização em
`RevendaApp.Web`, então `internal` exigiria `InternalsVisibleTo` — e `MarcarFiadoQuitado`, que
tem a mesma natureza operacional, já é `public`. O XML doc acima é o contrato.

Adicione também a asserção do replay aos testes de `VendaFiadoTests` (Task 1 não é revisada; este
teste novo pertence a esta task):

```csharp
[Fact]
public void DefinirValorPagoFiado_sobrescreve_e_e_idempotente()
{
    var venda = VendaFiado(100m);

    venda.DefinirValorPagoFiado(40m);
    venda.DefinirValorPagoFiado(40m);

    Assert.Equal(40m, venda.ValorPagoFiado);
    Assert.Equal(VendaStatus.FiadoAberto, venda.Status);
}

[Fact]
public void DefinirValorPagoFiado_acima_do_total_quita_sem_estourar()
{
    var venda = VendaFiado(100m);

    venda.DefinirValorPagoFiado(150m);

    Assert.Equal(100m, venda.ValorPagoFiado);
    Assert.Equal(VendaStatus.FiadoQuitado, venda.Status);
}
```

Em `RegistroAuditoria.Acoes`:

Em `RegistroAuditoria.Acoes`:

```csharp
/// <summary>Normalização única da quitação de fiado legado, executada no bootstrap.</summary>
public const string FiadoLegadoNormalizado = "FiadoLegadoNormalizado";
```

Em `RegistroAuditoria.Origens`:

```csharp
/// <summary>Ação executada pelo próprio sistema no bootstrap (sem usuário).</summary>
public const string Sistema = "Sistema";
```

- [ ] **Step 4: Implement `NormalizarFiadoLegado`**

```csharp
// src/RevendaApp.Web/Features/Fiado/Normalizacao/NormalizarFiadoLegado.cs
using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.Infrastructure;

namespace RevendaApp.Web.Features.Fiado.Normalizacao;

/// <summary>Resumo da normalização, para log e auditoria.</summary>
public sealed record ResultadoNormalizacao(int ClientesAnalisados, int VendasNormalizadas, int Divergencias);

/// <summary>
/// Reconstrói <see cref="Venda.ValorPagoFiado"/> das vendas fiado anteriores à coluna, a partir
/// do histórico imutável: pagamentos com <c>VendaId == null</c> são abatimentos de saldo devedor
/// e são replicados em FIFO sobre as vendas em aberto de cada cliente.
/// </summary>
/// <remarks>
/// Roda no bootstrap, uma única vez, marcada por <see cref="RegistroAuditoria.Acoes.FiadoLegadoNormalizado"/>.
/// É idempotente e não sobrescreve <see cref="Cliente.SaldoDevedor"/>: divergências são
/// registradas, não corrigidas à força.
/// </remarks>
public static class NormalizarFiadoLegado
{
    public static async Task<ResultadoNormalizacao> ExecutarAsync(
        AppDbContext db, CancellationToken cancellationToken)
    {
        var jaExecutou = await db.RegistrosAuditoria
            .AnyAsync(r => r.Acao == RegistroAuditoria.Acoes.FiadoLegadoNormalizado, cancellationToken);

        if (jaExecutou)
        {
            return new ResultadoNormalizacao(0, 0, 0);
        }

        var clientes = await db.Clientes.ToListAsync(cancellationToken);
        var vendasPorCliente = (await db.Vendas
                .Where(v => v.FormaPagamento == FormaPagamento.Fiado && v.ExcluidaEm == null)
                .OrderBy(v => v.DataHora)
                .Include(v => v.Itens)
                .ToListAsync(cancellationToken))
            .GroupBy(v => v.ClienteId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var pagamentosPorCliente = (await db.Pagamentos
                .Where(p => p.VendaId == null)
                .OrderBy(p => p.Data)
                .ToListAsync(cancellationToken))
            .GroupBy(p => p.ClienteId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var vendasNormalizadas = 0;
        var divergencias = 0;
        var detalhesDivergencia = new List<string>();

        foreach (var cliente in clientes)
        {
            if (!vendasPorCliente.TryGetValue(cliente.Id, out var vendas) || vendas.Count == 0)
            {
                continue;
            }

            // Replay determinístico: a soma dos pagamentos de fiado (VendaId null) é consumida
            // em FIFO pelas vendas em aberto. Cálculo do alvo por venda, depois um set absoluto —
            // assim rodar de novo produz exatamente o mesmo estado.
            var acumulado = pagamentosPorCliente.TryGetValue(cliente.Id, out var pagamentos)
                ? pagamentos.Sum(p => p.Valor)
                : 0m;

            foreach (var venda in vendas)
            {
                var alocado = Math.Min(acumulado, venda.ValorTotal);
                acumulado -= alocado;

                if (venda.ValorPagoFiado != alocado)
                {
                    venda.DefinirValorPagoFiado(alocado);
                    vendasNormalizadas++;
                }
            }

            var somaSaldoVendas = vendas.Sum(v => v.SaldoFiado);
            if (somaSaldoVendas != cliente.SaldoDevedor)
            {
                divergencias++;
                detalhesDivergencia.Add($"{cliente.Id}: saldo do cadastro {cliente.SaldoDevedor:F2}, vendas {somaSaldoVendas:F2}");
            }
        }

        var resumo = $"Clientes analisados: {clientes.Count}; vendas normalizadas: {vendasNormalizadas}; " +
                     $"divergências: {divergencias}";
        if (detalhesDivergencia.Count > 0)
        {
            resumo += "; " + string.Join(" | ", detalhesDivergencia);
        }

        db.RegistrosAuditoria.Add(new RegistroAuditoria(
            empresaId: null,
            usuarioId: null,
            usuarioNome: null,
            RegistroAuditoria.Acoes.FiadoLegadoNormalizado,
            resumo,
            RegistroAuditoria.Origens.Sistema));

        await db.SaveChangesAsync(cancellationToken);

        return new ResultadoNormalizacao(clientes.Count, vendasNormalizadas, divergencias);
    }
}
```

**Pontos que o corpo acima fixa e que não podem mudar:**
- `EmpresaId == null` e `Origens.Sistema` no marcador: é ação global, não de um tenant.
- O filtro `p.VendaId == null` é o que separa pagamento de fiado de pagamento no ato.
- `venda.DefinirValorPagoFiado(alocado)` (set absoluto), nunca `AbaterFiado`.
- `Cliente.SaldoDevedor` é **lido**, nunca escrito.
- `CancellationToken` propagado em todos os `await`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/RevendaApp.UnitTests --filter FullyQualifiedName~NormalizarFiadoLegadoTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/RevendaApp.Web/Features/Fiado/Normalizacao/NormalizarFiadoLegado.cs src/RevendaApp.Domain/Entities/RegistroAuditoria.cs tests/RevendaApp.UnitTests/Features/Fiado/NormalizarFiadoLegadoTests.cs
git commit -m "feat: normaliza quitacao de fiado legado a partir do historico"
```

---

### Task 7: Normalização roda no bootstrap na primeira subida

**Files:**
- Modify: `src/RevendaApp.Web/Program.cs:236-286` (bloco do bootstrap), `:318-325` (`LogsDoBootstrap`)
- Test: `tests/RevendaApp.IntegrationTests/NormalizacaoFiadoBootstrapTests.cs` (create)

**Interfaces:**
- Consumes: `NormalizarFiadoLegado.ExecutarAsync` (Task 6).
- Produces: bootstrap executa a normalização após `MigrateAsync`, antes do seed.

- [ ] **Step 1: Write the failing integration test**

```csharp
// tests/RevendaApp.IntegrationTests/NormalizacaoFiadoBootstrapTests.cs
[Fact]
public async Task Normalizacao_corrige_banco_legado_contra_Postgres()
{
    // Monta via CreateDbContext(): cliente + 2 vendas fiado (60, 40) + pagamento de fiado de 70
    // (Pagamento com VendaId null) e SaldoDevedor reduzido em 70.
    // Zera ValorPagoFiado das vendas (UPDATE direto via ExecuteSqlRaw no banco de teste, ou
    // recriando as vendas com ValorPagoFiado 0).
    // Chama NormalizarFiadoLegado.ExecutarAsync.
    // Espera: venda antiga FiadoQuitado, venda nova com ValorPagoFiado 10, e o marcador gravado.
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/RevendaApp.IntegrationTests --filter FullyQualifiedName~NormalizacaoFiadoBootstrapTests`
Expected: FAIL (cenário de zeração ou comportamento do Postgres).

- [ ] **Step 3: Wire into `Program.cs`**

Logo depois de `await dbContext.Database.MigrateAsync();` e antes do `SeedDefaultAdminUserAsync`:

```csharp
// Normalização única da quitação de fiado legado (Fase 0). Deriva de histórico imutável,
// é idempotente e grava um marcador em RegistroAuditoria. Falha não derruba o bootstrap:
// o app continua servindo e o relatório de fiado sinaliza os clientes divergentes.
LogsDoBootstrap.NormalizandoFiadoLegado(bootstrapLogger);
try
{
    var normalizacao = await NormalizarFiadoLegado.ExecutarAsync(dbContext, CancellationToken.None);
    LogsDoBootstrap.FiadoLegadoNormalizado(bootstrapLogger,
        normalizacao.ClientesAnalisados, normalizacao.VendasNormalizadas, normalizacao.Divergencias);
}
catch (Exception ex)
{
    bootstrapLogger.LogError(ex, "Falha ao normalizar a quitação de fiado legado; seguindo o bootstrap.");
}
```

E em `LogsDoBootstrap`:

```csharp
[LoggerMessage(Level = LogLevel.Information, Message = "Normalizando quitação de fiado legado...")]
public static partial void NormalizandoFiadoLegado(ILogger logger);

[LoggerMessage(Level = LogLevel.Information,
    Message = "Fiado legado normalizado: {Clientes} clientes, {Vendas} vendas, {Divergencias} divergências.")]
public static partial void FiadoLegadoNormalizado(ILogger logger, int clientes, int vendas, int divergencias);
```

Adicione o `using RevendaApp.Web.Features.Fiado.Normalizacao;` no topo do `Program.cs`.

- [ ] **Step 4: Run the test and the build**

Run: `dotnet test tests/RevendaApp.IntegrationTests --filter FullyQualifiedName~NormalizacaoFiadoBootstrapTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RevendaApp.Web/Program.cs tests/RevendaApp.IntegrationTests/NormalizacaoFiadoBootstrapTests.cs
git commit -m "feat: executa normalizacao de fiado legado no bootstrap"
```

---

# Fase 2 — área de relatórios e estoque por dia

### Task 8: `PeriodoMes` como fonte única do mês civil

**Files:**
- Create: `src/RevendaApp.Web/Common/Relatorios/PeriodoMes.cs`
- Test: `tests/RevendaApp.UnitTests/Common/Relatorios/PeriodoMesTests.cs` (create)

**Interfaces:**
- Consumes: `BrasilTimeZone`.
- Produces: `PeriodoMes(int Ano, int Mes)` com `InicioUtc`, `FimUtc`, `DiasNoMes`, `Rotulo`, `DoDia(DateTime)`, `Anterior()`, `Proximo()`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/RevendaApp.UnitTests/Common/Relatorios/PeriodoMesTests.cs
using RevendaApp.Web.Common.Relatorios;

namespace RevendaApp.UnitTests.Common.Relatorios;

public class PeriodoMesTests
{
    [Fact]
    public void PeriodoMes_calcula_intervalo_utc_do_mes_de_brasilia()
    {
        var periodo = new PeriodoMes(2026, 11);

        // 1º de novembro 00:00 BRT = 03:00 UTC; 1º de dezembro 00:00 BRT = 03:00 UTC (exclusivo).
        Assert.Equal(new DateTime(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc), periodo.InicioUtc);
        Assert.Equal(new DateTime(2026, 12, 1, 3, 0, 0, DateTimeKind.Utc), periodo.FimUtc);
    }

    [Theory]
    [InlineData(2026, 11, 30)]
    [InlineData(2026, 2, 28)]
    [InlineData(2028, 2, 29)]
    [InlineData(2026, 4, 30)]
    [InlineData(2026, 12, 31)]
    public void PeriodoMes_calcula_dias_do_mes(int ano, int mes, int esperado)
    {
        Assert.Equal(esperado, new PeriodoMes(ano, mes).DiasNoMes);
    }

    [Fact]
    public void PeriodoMes_Dezembro_vira_janeiro_ao_avancar()
    {
        var proximo = new PeriodoMes(2026, 12).Proximo();
        Assert.Equal(new PeriodoMes(2027, 1), proximo);
    }

    [Fact]
    public void PeriodoMes_Janeiro_volta_para_dezembro_do_ano_anterior()
    {
        Assert.Equal(new PeriodoMes(2025, 12), new PeriodoMes(2026, 1).Anterior());
    }

    [Fact]
    public void PeriodoMes_DoDia_usa_o_mes_civil_de_brasilia()
    {
        // 01/12/2026 01:00 UTC ainda é 30/11/2026 22:00 em Brasília.
        var periodo = PeriodoMes.DoDia(new DateTime(2026, 12, 1, 1, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new PeriodoMes(2026, 11), periodo);
    }

    [Theory]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    public void PeriodoMes_rejeita_mes_fora_de_1_a_12(int ano, int mes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeriodoMes(ano, mes));
    }

    [Fact]
    public void PeriodoMes_tem_rotulo_em_pt_br()
    {
        Assert.Equal("novembro/2026", new PeriodoMes(2026, 11).Rotulo);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RevendaApp.UnitTests --filter FullyQualifiedName~PeriodoMesTests`
Expected: FAIL — `PeriodoMes` não existe.

- [ ] **Step 3: Implement `PeriodoMes`**

```csharp
// src/RevendaApp.Web/Common/Relatorios/PeriodoMes.cs
using System.Globalization;

namespace RevendaApp.Web.Common.Relatorios;

/// <summary>
/// Um mês civil de Brasília, com o intervalo UTC correspondente. Fonte única para os relatórios:
/// nenhum handler monta o intervalo na mão.
/// </summary>
public readonly record struct PeriodoMes
{
    public PeriodoMes(int ano, int mes)
    {
        if (mes is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(mes), "O mês deve estar entre 1 e 12.");
        }

        Ano = ano;
        Mes = mes;
    }

    public int Ano { get; }
    public int Mes { get; }

    /// <summary>Primeiro instante do mês em UTC (dia 1, 00:00 de Brasília).</summary>
    public DateTime InicioUtc => BrasilTimeZone.ParaUtc(new DateTime(Ano, Mes, 1));

    /// <summary>Primeiro instante do mês seguinte em UTC — fim <b>exclusivo</b> do intervalo.</summary>
    public DateTime FimUtc => BrasilTimeZone.ParaUtc(new DateTime(Ano, Mes, 1).AddMonths(1));

    public int DiasNoMes => DateTime.DaysInMonth(Ano, Mes);

    public string Rotulo => $"{NomeDoMes}/{Ano}";

    private string NomeDoMes => new DateTime(Ano, Mes, 1)
        .ToString("MMMM", CultureInfo.GetCultureInfo("pt-BR"));

    /// <summary>Mês civil de Brasília a que um instante UTC pertence.</summary>
    public static PeriodoMes DoDia(DateTime utc)
    {
        var brasilia = BrasilTimeZone.ParaBrasilia(utc);
        return new PeriodoMes(brasilia.Year, brasilia.Month);
    }

    public PeriodoMes Anterior() => Mes == 1 ? new PeriodoMes(Ano - 1, 12) : new PeriodoMes(Ano, Mes - 1);

    public PeriodoMes Proximo() => Mes == 12 ? new PeriodoMes(Ano + 1, 1) : new PeriodoMes(Ano, Mes + 1);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RevendaApp.UnitTests --filter FullyQualifiedName~PeriodoMesTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RevendaApp.Web/Common/Relatorios/PeriodoMes.cs tests/RevendaApp.UnitTests/Common/Relatorios/PeriodoMesTests.cs
git commit -m "feat: adiciona periodo de mes civil de brasilia para relatorios"
```

---

### Task 9: Relatório de estoque por dia

**Files:**
- Create: `src/RevendaApp.Web/Features/Relatorios/Estoque/ObterRelatorioEstoqueQuery.cs`
- Create: `src/RevendaApp.Web/Features/Relatorios/Estoque/ObterRelatorioEstoqueQueryHandler.cs`
- Test: `tests/RevendaApp.UnitTests/Features/Relatorios/ObterRelatorioEstoqueQueryHandlerTests.cs` (create)

**Interfaces:**
- Consumes: `PeriodoMes`, `BrasilTimeZone`, `tenant.EmpresaIdRequerida`.
- Produces:
  - `ObterRelatorioEstoqueQuery(PeriodoMes Periodo) : IQuery<RelatorioEstoqueResult>`
  - `RelatorioEstoqueResult(PeriodoMes Periodo, IReadOnlyList<ProdutoNoMes> Produtos, IReadOnlyList<AjusteNoMes> Ajustes, IReadOnlyList<MovimentoDoDia> PorDia)`
  - `ProdutoNoMes(Guid ProdutoId, string Nome, bool Ativo, IReadOnlyList<int> EntradasPorDia, IReadOnlyList<int> SaidasPorDia, int TotalEntradas, int TotalSaidas, int MovimentoLiquido, int EstoqueAtual, int QuantidadeAjustes)`
  - `AjusteNoMes(Guid ProdutoId, string ProdutoNome, int Dia, int NovoTotal, string? Motivo)`
  - `MovimentoDoDia(int Dia, int Entradas, int Saidas)`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/RevendaApp.UnitTests/Features/Relatorios/ObterRelatorioEstoqueQueryHandlerTests.cs
using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.UnitTests.TestHelpers;
using RevendaApp.Web.Common.Relatorios;
using RevendaApp.Web.Features.Relatorios.Estoque;

namespace RevendaApp.UnitTests.Features.Relatorios;

public class ObterRelatorioEstoqueQueryHandlerTests
{
    private static async Task<(Infrastructure.AppDbContext Db, Produto Produto)> CenarioAsync(params MovimentacaoEstoque[] movimentos)
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var produto = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m);
        produto.RegistrarEntrada(50);
        db.Produtos.Add(produto);
        foreach (var m in movimentos)
        {
            db.MovimentacoesEstoque.Add(m);
        }
        await db.SaveChangesAsync();
        return (db, produto);
    }

    [Fact]
    public async Task Estoque_agrupa_entradas_e_saidas_por_dia_de_brasilia()
    {
        var produtoId = Guid.NewGuid();
        // 02/11 02:00 UTC = 01/11 23:00 BRT → conta no dia 1.
        var (db, _) = await CenarioAsync(
            new MovimentacaoEstoque(produtoId, TipoMovimentacaoEstoque.Saida, 3, "Venda",
                new DateTime(2026, 11, 2, 2, 0, 0, DateTimeKind.Utc)));
        var handler = new ObterRelatorioEstoqueQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioEstoqueQuery(new PeriodoMes(2026, 11)), CancellationToken.None);

        var linha = resultado.Produtos.Single();
        Assert.Equal(3, linha.SaidasPorDia[0]);   // índice 0 = dia 1
        Assert.Equal(0, linha.SaidasPorDia[1]);
    }

    [Fact]
    public async Task Estoque_exclui_ajustes_da_matriz_e_lista_em_ajustes()
    {
        var produtoId = Guid.NewGuid();
        var (db, _) = await CenarioAsync(
            new MovimentacaoEstoque(produtoId, TipoMovimentacaoEstoque.Ajuste, 42, "Inventário",
                new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc)),
            new MovimentacaoEstoque(produtoId, TipoMovimentacaoEstoque.Entrada, 5, "Compra",
                new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc)));
        var handler = new ObterRelatorioEstoqueQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioEstoqueQuery(new PeriodoMes(2026, 11)), CancellationToken.None);

        var linha = resultado.Produtos.Single();
        Assert.Equal(5, linha.TotalEntradas);
        Assert.Equal(0, linha.TotalSaidas);
        Assert.Equal(1, linha.QuantidadeAjustes);
        var ajuste = Assert.Single(resultado.Ajustes);
        Assert.Equal(10, ajuste.Dia);
        Assert.Equal(42, ajuste.NovoTotal);
    }

    [Fact]
    public async Task Estoque_calcula_totais_liquido_e_estoque_atual()
    {
        var produtoId = Guid.NewGuid();
        var (db, _) = await CenarioAsync(
            new MovimentacaoEstoque(produtoId, TipoMovimentacaoEstoque.Entrada, 10, "Compra",
                new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc)),
            new MovimentacaoEstoque(produtoId, TipoMovimentacaoEstoque.Saida, 4, "Venda",
                new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc)));
        var handler = new ObterRelatorioEstoqueQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioEstoqueQuery(new PeriodoMes(2026, 11)), CancellationToken.None);

        var linha = resultado.Produtos.Single();
        Assert.Equal(10, linha.TotalEntradas);
        Assert.Equal(4, linha.TotalSaidas);
        Assert.Equal(6, linha.MovimentoLiquido);
        Assert.Equal(50, linha.EstoqueAtual);   // estado de hoje, não do mês
    }

    [Fact]
    public async Task Estoque_inclui_inativo_sem_movimento_com_vetores_zerados()
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var produto = new Produto("Desativado", TipoProduto.Agua, 10m, 5m, 8m);
        produto.Desativar();
        db.Produtos.Add(produto);
        await db.SaveChangesAsync();
        var handler = new ObterRelatorioEstoqueQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioEstoqueQuery(new PeriodoMes(2026, 11)), CancellationToken.None);

        var linha = Assert.Single(resultado.Produtos);
        Assert.False(linha.Ativo);
        Assert.All(linha.EntradasPorDia, v => Assert.Equal(0, v));
        Assert.Equal(0, linha.MovimentoLiquido);
    }

    [Fact]
    public async Task Estoque_mes_sem_movimento_retorna_matriz_do_tamanho_do_mes()
    {
        var (db, _) = await CenarioAsync();
        var handler = new ObterRelatorioEstoqueQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioEstoqueQuery(new PeriodoMes(2026, 11)), CancellationToken.None);

        var linha = resultado.Produtos.Single();
        Assert.Equal(30, linha.EntradasPorDia.Count);
        Assert.Equal(30, linha.SaidasPorDia.Count);
        Assert.Empty(resultado.Ajustes);
    }

    [Fact]
    public async Task Estoque_soma_por_dia_entre_produtos()
    {
        var db = InMemoryDbContextFactory.Create(TenantTeste.EmpresaId);
        var gas = new Produto("Botijão 13kg", TipoProduto.Gas, 100m, 60m, 80m);
        var agua = new Produto("Água 20L", TipoProduto.Agua, 20m, 10m, 15m);
        db.Produtos.AddRange(gas, agua);
        db.MovimentacoesEstoque.AddRange(
            new MovimentacaoEstoque(gas.Id, TipoMovimentacaoEstoque.Saida, 2, "Venda",
                new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc)),
            new MovimentacaoEstoque(agua.Id, TipoMovimentacaoEstoque.Saida, 5, "Venda",
                new DateTime(2026, 11, 3, 14, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        var handler = new ObterRelatorioEstoqueQueryHandler(db, TenantTeste.Contexto());

        var resultado = await handler.Handle(new ObterRelatorioEstoqueQuery(new PeriodoMes(2026, 11)), CancellationToken.None);

        var dia3 = resultado.PorDia.Single(d => d.Dia == 3);
        Assert.Equal(7, dia3.Saidas);
        Assert.Equal(30, resultado.PorDia.Count);
    }

    [Fact]
    public async Task Estoque_sem_empresa_selecionada_lanca()
    {
        var tenant = new FakeTenantContext(empresaId: null, ehSuperAdmin: true);
        await using var db = InMemoryDbContextFactory.Create(tenant: tenant);
        var handler = new ObterRelatorioEstoqueQueryHandler(db, tenant);

        var excecao = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(new ObterRelatorioEstoqueQuery(new PeriodoMes(2026, 11)), CancellationToken.None).AsTask());

        Assert.Contains("exige uma empresa", excecao.Message);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RevendaApp.UnitTests --filter FullyQualifiedName~ObterRelatorioEstoqueQueryHandlerTests`
Expected: FAIL — tipos não existem.

- [ ] **Step 3: Create the query contract**

```csharp
// src/RevendaApp.Web/Features/Relatorios/Estoque/ObterRelatorioEstoqueQuery.cs
using Mediator;

using RevendaApp.Web.Common.Relatorios;

namespace RevendaApp.Web.Features.Relatorios.Estoque;

/// <summary>Entradas e saídas de estoque por produto, dia a dia, num mês civil.</summary>
public sealed record ObterRelatorioEstoqueQuery(PeriodoMes Periodo) : IQuery<RelatorioEstoqueResult>;

public sealed record RelatorioEstoqueResult(
    PeriodoMes Periodo,
    IReadOnlyList<ProdutoNoMes> Produtos,
    IReadOnlyList<AjusteNoMes> Ajustes,
    IReadOnlyList<MovimentoDoDia> PorDia);

/// <summary>
/// Movimento de um produto no mês. Os vetores têm um item por dia (índice 0 = dia 1).
/// <see cref="MovimentoLiquido"/> é entrada − saída no mês; <see cref="EstoqueAtual"/> é o
/// estoque de hoje, não o do mês (ajustes não são reconstruíveis).
/// </summary>
public sealed record ProdutoNoMes(
    Guid ProdutoId,
    string Nome,
    bool Ativo,
    IReadOnlyList<int> EntradasPorDia,
    IReadOnlyList<int> SaidasPorDia,
    int TotalEntradas,
    int TotalSaidas,
    int MovimentoLiquido,
    int EstoqueAtual,
    int QuantidadeAjustes);

/// <summary>Ajuste de estoque do mês: registra o <b>novo total</b>, não o delta.</summary>
public sealed record AjusteNoMes(Guid ProdutoId, string ProdutoNome, int Dia, int NovoTotal, string? Motivo);

public sealed record MovimentoDoDia(int Dia, int Entradas, int Saidas);
```

- [ ] **Step 4: Implement the handler**

Regras que o handler precisa respeitar:
- primeira instrução `_ = tenant.EmpresaIdRequerida("obter relatório de estoque")`;
- carregar `Produtos` (todos, inclusive inativos) e as `MovimentacoesEstoque` com
  `Data >= Periodo.InicioUtc && Data < Periodo.FimUtc`, ambos `AsNoTracking()`;
- agrupar **após materializar**, convertendo com `BrasilTimeZone.ParaBrasilia` e usando
  `Dia = brasilia.Day` — o EF não traduz `TimeZoneInfo`;
- `Ajuste` não entra em `EntradasPorDia`/`SaidasPorDia`, só em `Ajustes` e em
  `QuantidadeAjustes`;
- produto sem movimento sai com vetores de `Periodo.DiasNoMes` zeros;
- `PorDia` tem uma entrada por dia do mês (dias sem movimento com zeros), somando entre produtos;
- ordenar produtos por `Nome`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/RevendaApp.UnitTests --filter FullyQualifiedName~ObterRelatorioEstoqueQueryHandlerTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/RevendaApp.Web/Features/Relatorios/Estoque/ tests/RevendaApp.UnitTests/Features/Relatorios/ObterRelatorioEstoqueQueryHandlerTests.cs
git commit -m "feat: adiciona relatorio de estoque por dia"
```

---

### Task 10: Página do relatório de estoque, shell e landing

**Files:**
- Create: `src/RevendaApp.Web/Features/Relatorios/Shell/RelatorioShell.razor`
- Create: `src/RevendaApp.Web/Features/Relatorios/Home/RelatoriosHome.razor`
- Create: `src/RevendaApp.Web/Features/Relatorios/Estoque/RelatorioEstoque.razor`
- Modify: `src/RevendaApp.Web/Components/Layout/MainLayout.razor:40-46`
- Modify: `src/RevendaApp.Web/Features/Estoque/Listar/EstoqueLista.razor:23-33`
- Test: `tests/RevendaApp.UnitTests/Components/RelatoriosHomeTests.cs` (create)

**Interfaces:**
- Consumes: `ObterRelatorioEstoqueQuery`, `PeriodoMes`.
- Produces: rotas `/relatorios` e `/relatorios/estoque`.

- [ ] **Step 1: Write the failing component test**

```csharp
// tests/RevendaApp.UnitTests/Components/RelatoriosHomeTests.cs
// Segue o padrão de EstoqueListaAdminTests: bUnit com DI mínima.
[Fact]
public void RelatoriosHome_lista_os_quatro_relatorios_com_link()
{
    // Renderiza RelatoriosHome e verifica que o markup contém os hrefs
    // /relatorios/estoque, /relatorios/vendas, /relatorios/fiado, /relatorios/vasilhames
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/RevendaApp.UnitTests --filter FullyQualifiedName~RelatoriosHomeTests`
Expected: FAIL — componente não existe.

- [ ] **Step 3: Implement the shell, the landing and the page**

`RelatorioShell.razor` (sem `@rendermode`; é usado dentro de páginas interativas):

```razor
@using RevendaApp.Web.Common.Relatorios

<MudPaper Class="pa-4 mb-4">
    <div class="d-flex flex-wrap align-center gap-4">
        <div>
            <MudText Typo="Typo.h5">@Titulo</MudText>
            <MudText Typo="Typo.body2">@Descricao</MudText>
        </div>
        <MudSpacer />
        <MudSelect T="int" Label="Mês" Value="Mes" ValueChanged="OnMesChangedAsync" Dense="true" Style="max-width: 160px;">
            @for (var m = 1; m <= 12; m++)
            {
                <MudSelectItem T="int" Value="m">@NomeMes(m)</MudSelectItem>
            }
        </MudSelect>
        <MudSelect T="int" Label="Ano" Value="Ano" ValueChanged="OnAnoChangedAsync" Dense="true" Style="max-width: 120px;">
            @for (var a = DateTime.Now.Year; a >= DateTime.Now.Year - 4; a--)
            {
                <MudSelectItem T="int" Value="a">@a</MudSelectItem>
            }
        </MudSelect>
    </div>
</MudPaper>

@ChildContent

@code {
    [Parameter, EditorRequired] public string Titulo { get; set; } = "";
    [Parameter] public string Descricao { get; set; } = "";
    [Parameter] public int Mes { get; set; }
    [Parameter] public int Ano { get; set; }
    [Parameter] public EventCallback<PeriodoMes> OnPeriodoAlterado { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }

    private Task OnMesChangedAsync(int mes) => OnPeriodoAlterado.InvokeAsync(new PeriodoMes(Ano, mes));
    private Task OnAnoChangedAsync(int ano) => OnPeriodoAlterado.InvokeAsync(new PeriodoMes(ano, Mes));

    private static string NomeMes(int mes) =>
        new DateTime(2000, mes, 1).ToString("MMMM", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
}
```

`RelatoriosHome.razor`:

```razor
@page "/relatorios"
@attribute [Authorize(Policy = RevendaApp.Web.Common.Authorization.Politicas.ApenasEmpresa)]
@using RevendaApp.Web.Common
@inject IdentidadeEmpresaService Identidade

<PageTitle>@Identidade.Titulo("Relatórios")</PageTitle>

<MudText Typo="Typo.h4" Class="mb-4">Relatórios</MudText>

<MudGrid>
    @foreach (var relatorio in Relatorios)
    {
        <MudItem xs="12" sm="6" md="4">
            <MudPaper Class="pa-4" Style="height:100%">
                <MudText Typo="Typo.h6">@relatorio.Titulo</MudText>
                <MudText Typo="Typo.body2" Class="mb-4">@relatorio.Descricao</MudText>
                <MudButton Variant="Variant.Filled" Color="Color.Primary" Href="@relatorio.Rota">Abrir</MudButton>
            </MudPaper>
        </MudItem>
    }
</MudGrid>

@code {
    private static readonly (string Titulo, string Descricao, string Rota)[] Relatorios =
    [
        ("Estoque por dia", "Entradas e saídas de cada produto, dia a dia no mês.", "/relatorios/estoque"),
        ("Vendas do mês", "Faturamento, lucro, ticket e desconto por dia e por forma de pagamento.", "/relatorios/vendas"),
        ("Fiado", "Saldo a receber por cliente, faixas de atraso e recebimentos do mês.", "/relatorios/fiado"),
        ("Vasilhames", "Vasilhames na rua por cliente e produto, e devoluções do mês.", "/relatorios/vasilhames"),
    ];
}
```

`RelatorioEstoque.razor` (resumo do que a página faz; o markup detalhado é do implementador):
- `@page "/relatorios/estoque"`, `@attribute [Authorize(Policy = Politicas.ApenasEmpresa)]`,
  `@rendermode InteractiveServer`;
- usa `RelatorioShell` com `Titulo="Estoque por dia"` e recarrega a query quando
  `OnPeriodoAlterado` dispara;
- cartões de resumo (total de entradas, total de saídas, movimento líquido, nº de ajustes);
- `MudChart ChartType.Line` com séries "Entradas" e "Saídas" e rótulos dos dias;
- `MudTable` com `<div style="overflow-x:auto">` envolvendo: coluna Produto (com `MudChip`
  "Inativo" quando `!Ativo`), uma coluna por dia (`+N / −N`, vazio quando zero), Total entradas,
  Total saídas, Líquido, Estoque atual, Ajustes;
- `MudExpansionPanel` "Ajustes do mês" com produto | dia | novo total | motivo;
- mês inicial: `PeriodoMes.DoDia(DateTime.UtcNow)`.

- [ ] **Step 4: Add the navigation entries**

`MainLayout.razor`, dentro do `MudButtonGroup` de domínio (após o botão Estoque):

```razor
<MudButton Href="/relatorios" StartIcon="@Icons.Material.Filled.Assessment">Relatórios</MudButton>
```

`EstoqueLista.razor`, no `<div class="d-flex align-center mb-4">` do cabeçalho, antes do botão
"Novo Produto":

```razor
<MudButton Variant="Variant.Outlined" Color="Color.Primary" StartIcon="@Icons.Material.Filled.Assessment" Href="/relatorios/estoque" Class="mr-2">
    Relatório do mês
</MudButton>
```

- [ ] **Step 5: Run the component test and build**

Run: `dotnet test tests/RevendaApp.UnitTests --filter FullyQualifiedName~RelatoriosHomeTests`
Expected: PASS.

Run: `timeout 180s dotnet build`
Expected: build limpo.

- [ ] **Step 6: Commit**

```bash
git add src/RevendaApp.Web/Features/Relatorios/ src/RevendaApp.Web/Components/Layout/MainLayout.razor src/RevendaApp.Web/Features/Estoque/Listar/EstoqueLista.razor tests/RevendaApp.UnitTests/Components/RelatoriosHomeTests.cs
git commit -m "feat: adiciona area de relatorios com tela de estoque por dia"
```

---

### Task 11: Índices compostos para os relatórios

**Files:**
- Modify: `src/RevendaApp.Infrastructure/Configurations/MovimentacaoEstoqueConfiguration.cs`
- Modify: `src/RevendaApp.Infrastructure/Configurations/HistoricoEstoqueConfiguration.cs`
- Modify: `src/RevendaApp.Infrastructure/Configurations/EmprestimoVasilhameConfiguration.cs`
- Create: `src/RevendaApp.Infrastructure/Migrations/<timestamp>_IndicesRelatorios.cs` (gerada)

**Interfaces:**
- Consumes: nada.
- Produces: índices `IX_MovimentacoesEstoque_EmpresaId_Data`, `IX_HistoricosEstoque_EmpresaId_Data`, `IX_EmprestimosVasilhames_EmpresaId_DevolvidoEm`.

- [ ] **Step 1: Replace the simple tenant indexes with composites**

Em cada configuração, troque `builder.HasIndex(x => x.EmpresaId);` por:

```csharp
// Índice composto para os relatórios mensais: filtra por empresa e período. Substitui o
// índice simples de EmpresaId — o composto cobre o prefixo.
builder.HasIndex(m => new { m.EmpresaId, m.Data });
```

(em `HistoricoEstoqueConfiguration`: `h`; em `EmprestimoVasilhameConfiguration`:
`new { e.EmpresaId, e.DevolvidoEm }`, mantendo o `HasIndex(e => new { e.ClienteId, e.ProdutoId })`.)

- [ ] **Step 2: Generate the migration**

```bash
dotnet-ef migrations add IndicesRelatorios --project src/RevendaApp.Infrastructure --startup-project src/RevendaApp.Infrastructure -o Migrations
```

Confira que o `Up` contém os `DropIndex` dos índices simples e os `CreateIndex` compostos —
mesmo formato de `IndicesDashboard`.

- [ ] **Step 3: Verify tests still pass**

Run: `dotnet test tests/RevendaApp.IntegrationTests --filter FullyQualifiedName~RelatorioEstoque`
Expected: PASS (a migração aplica no Postgres do fixture).

- [ ] **Step 4: Commit**

```bash
git add src/RevendaApp.Infrastructure/Configurations/ src/RevendaApp.Infrastructure/Migrations/
git commit -m "perf: adiciona indices compostos para os relatorios mensais"
```

---

# Fase 3 — relatório de vendas

### Task 12: Relatório de vendas do mês

**Files:**
- Create: `src/RevendaApp.Web/Features/Relatorios/Vendas/ObterRelatorioVendasQuery.cs`
- Create: `src/RevendaApp.Web/Features/Relatorios/Vendas/ObterRelatorioVendasQueryHandler.cs`
- Create: `src/RevendaApp.Web/Features/Relatorios/Vendas/RelatorioVendas.razor`
- Test: `tests/RevendaApp.UnitTests/Features/Relatorios/ObterRelatorioVendasQueryHandlerTests.cs` (create)

**Interfaces:**
- Consumes: `PeriodoMes`, `VendaLucro.Reconhecido`, `EnumRotulos.Rotulo(FormaPagamento)`.
- Produces:
  - `ObterRelatorioVendasQuery(PeriodoMes Periodo) : IQuery<RelatorioVendasResult>`
  - `RelatorioVendasResult(PeriodoMes Periodo, IReadOnlyList<VendaDoDia> PorDia, IReadOnlyList<VendaPorFormaNoMes> PorForma, decimal FaturamentoTotal, decimal LucroTotal, decimal DescontoTotal, int QuantidadeVendas, decimal TicketMedio)`
  - `VendaDoDia(int Dia, int Quantidade, decimal Faturamento, decimal Lucro, decimal Desconto)`
  - `VendaPorFormaNoMes(FormaPagamento Forma, int Quantidade, decimal Valor)`

- [ ] **Step 1: Write the failing tests**

Montagem: `InMemoryDbContextFactory.Create(TenantTeste.EmpresaId)`, cliente, produto com custo 60
e preço 100, vendas criadas com `new Venda(clienteId, forma, dataHoraUtc)` + `AdicionarItem`.
Mês do cenário: `new PeriodoMes(2026, 11)`.

| Teste | Cenário | Asserção exata |
|---|---|---|
| `Vendas_agrupa_por_dia_de_brasilia` | venda 02/11 02:00 UTC (01/11 23:00 BRT) | `PorDia.Single(d => d.Dia == 1).Quantidade == 1` |
| `Vendas_nao_reconhece_lucro_de_fiado_em_aberto` | 1 venda fiado 100 | `LucroTotal == 0m` |
| `Vendas_reconhece_lucro_proporcional_ao_pago_no_fiado` | fiado 100, `AbaterFiado(50m)` | `LucroTotal == 20m` |
| `Vendas_exclui_venda_excluida_logicamente` | 1 venda paga + `venda.Excluir()` | `QuantidadeVendas == 0` e `FaturamentoTotal == 0m` |
| `Vendas_calcula_ticket_medio_e_zero_sem_vendas` | nenhuma venda | `TicketMedio == 0m` (sem `DivideByZeroException`) |
| `Vendas_calcula_ticket_medio` | 2 vendas de 100 e 50 | `TicketMedio == 75m` |
| `Vendas_agrupa_por_forma_de_pagamento` | 1 Pix 100 + 1 Dinheiro 50 | `PorForma` tem 2 linhas, `Valor` 100 e 50 |
| `Vendas_sem_empresa_selecionada_lanca` | `new FakeTenantContext(null, ehSuperAdmin: true)` | `InvalidOperationException` com "exige uma empresa" |

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RevendaApp.UnitTests --filter FullyQualifiedName~ObterRelatorioVendasQueryHandlerTests`
Expected: FAIL — tipos não existem.

- [ ] **Step 3: Implement query, handler and page**

- Handler: `EmpresaIdRequerida` primeiro; `Vendas` com `ExcluidaEm == null` e
  `DataHora` no intervalo, `Include(v => v.Itens)`, `AsNoTracking()`; agrupar por
  `BrasilTimeZone.ParaBrasilia(v.DataHora).Day` após materializar; lucro via
  `VendaLucro.Reconhecido`; `TicketMedio = QuantidadeVendas == 0 ? 0 : FaturamentoTotal / QuantidadeVendas`.
- Página: `@page "/relatorios/vendas"`, `InteractiveServer`, `ApenasEmpresa`, `RelatorioShell`,
  cartões (faturamento, lucro, ticket, nº vendas), `MudChart` Line (faturamento e lucro por dia)
  e `MudTable` por forma de pagamento.

- [ ] **Step 4: Run tests and build**

Run: `dotnet test tests/RevendaApp.UnitTests --filter FullyQualifiedName~ObterRelatorioVendasQueryHandlerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RevendaApp.Web/Features/Relatorios/Vendas/ tests/RevendaApp.UnitTests/Features/Relatorios/ObterRelatorioVendasQueryHandlerTests.cs
git commit -m "feat: adiciona relatorio de vendas do mes"
```

---

# Fase 4 — relatório de fiado

### Task 13: Relatório de fiado com aging

**Files:**
- Create: `src/RevendaApp.Web/Features/Relatorios/Fiado/ObterRelatorioFiadoQuery.cs`
- Create: `src/RevendaApp.Web/Features/Relatorios/Fiado/ObterRelatorioFiadoQueryHandler.cs`
- Create: `src/RevendaApp.Web/Features/Relatorios/Fiado/RelatorioFiado.razor`
- Test: `tests/RevendaApp.UnitTests/Features/Relatorios/ObterRelatorioFiadoQueryHandlerTests.cs` (create)

**Interfaces:**
- Consumes: `PeriodoMes`, `Venda.SaldoFiado`, `BrasilTimeZone`, `TimeProvider`.
- Produces:
  - `ObterRelatorioFiadoQuery(PeriodoMes Periodo) : IQuery<RelatorioFiadoResult>`
  - `RelatorioFiadoResult(PeriodoMes Periodo, decimal TotalAReceber, IReadOnlyList<FaixaAging> Faixas, IReadOnlyList<ClienteDevedorItem> Clientes, IReadOnlyList<RecebimentoDoDia> RecebimentosPorDia, IReadOnlyList<RecebimentoPorForma> RecebimentosPorForma, decimal TotalRecebido)`
  - `FaixaAging(string Rotulo, int Clientes, decimal Valor)`
  - `ClienteDevedorItem(Guid ClienteId, string Nome, string Telefone, decimal Saldo, int DiasAtraso, DateTime? VendaMaisAntigaEmAberto, bool TemDivergenciaDeSaldo)`
  - `RecebimentoDoDia(int Dia, decimal Valor)`, `RecebimentoPorForma(FormaPagamento Forma, decimal Valor)`

- [ ] **Step 1: Write the failing tests**

O handler recebe `TimeProvider` (use `FakeTimeProvider` fixo em `2026-11-17T12:00:00Z`, ou o
`TimeProvider.System` quando a asserção não depender de "hoje").

| Teste | Cenário | Asserção exata |
|---|---|---|
| `Fiado_usa_saldo_fiado_e_nao_o_valor_total_no_aging` | fiado 100 com `AbaterFiado(60m)`; `SaldoDevedor = 40` | `Clientes.Single().Saldo == 40m` |
| `Fiado_calcula_faixas_de_atraso_pela_venda_mais_antiga_em_aberto` | venda há 45 dias, saldo 100 | `Faixas.Single(f => f.Clientes > 0).Rotulo == "31–60"` e `Clientes.Single().DiasAtraso == 45` |
| `Fiado_marca_aviso_quando_saldo_do_cliente_difere_das_vendas` | fiado 100, `SaldoDevedor = 30` (ajuste manual) | `TemDivergenciaDeSaldo == true` |
| `Fiado_soma_recebimentos_do_mes_por_forma` | 1 `Pagamento` de 50 em dinheiro no mês | `TotalRecebido == 50m` e `RecebimentosPorForma.Single().Valor == 50m` |
| `Fiado_mes_sem_movimento_retorna_zerado` | só um devedor, sem pagamento no mês | `TotalRecebido == 0m`, `RecebimentosPorDia.Count == DiasNoMes`, todas as faixas com 0 exceto a do devedor |
| `Fiado_sem_empresa_selecionada_lanca` | tenant central | `InvalidOperationException` com "exige uma empresa" |

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RevendaApp.UnitTests --filter FullyQualifiedName~ObterRelatorioFiadoQueryHandlerTests`
Expected: FAIL.

- [ ] **Step 3: Implement query, handler and page**

- Handler: `EmpresaIdRequerida` primeiro; clientes com `SaldoDevedor > 0`; para cada um, carregar
  as vendas fiado abertas (`Include(Itens)`) e usar `venda.SaldoFiado` como valor em aberto;
  `DiasAtraso` = `(hojeBrasilia - BrasilTimeZone.ParaBrasilia(vendaMaisAntiga.DataHora).Date).Days`
  com `TimeProvider`; `TemDivergenciaDeSaldo` = `vendas.Sum(v => v.SaldoFiado) != cliente.SaldoDevedor`;
  faixas 0–30, 31–60, 61–90, 90+; recebimentos de `Pagamento` no intervalo, por dia e por forma.
- Página: `@page "/relatorios/fiado"`, `InteractiveServer`, `ApenasEmpresa`, `RelatorioShell`,
  cartões (total a receber, total recebido no mês), tabela de clientes com chip de faixa e ícone
  de alerta quando `TemDivergenciaDeSaldo` (tooltip "saldo do cadastro difere das vendas em
  aberto"), gráfico de recebimentos por dia e tabela por forma.

- [ ] **Step 4: Run tests and build**

Run: `dotnet test tests/RevendaApp.UnitTests --filter FullyQualifiedName~ObterRelatorioFiadoQueryHandlerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RevendaApp.Web/Features/Relatorios/Fiado/ tests/RevendaApp.UnitTests/Features/Relatorios/ObterRelatorioFiadoQueryHandlerTests.cs
git commit -m "feat: adiciona relatorio de fiado com faixas de atraso"
```

---

# Fase 5 — relatório de vasilhames

### Task 14: Relatório de vasilhames

**Files:**
- Create: `src/RevendaApp.Web/Features/Relatorios/Vasilhames/ObterRelatorioVasilhamesQuery.cs`
- Create: `src/RevendaApp.Web/Features/Relatorios/Vasilhames/ObterRelatorioVasilhamesQueryHandler.cs`
- Create: `src/RevendaApp.Web/Features/Relatorios/Vasilhames/RelatorioVasilhames.razor`
- Test: `tests/RevendaApp.UnitTests/Features/Relatorios/ObterRelatorioVasilhamesQueryHandlerTests.cs` (create)

**Interfaces:**
- Consumes: `PeriodoMes`, `EmprestimoVasilhame`, `HistoricoEstoque` com `Tipo == DevolucaoVasilhame`.
- Produces:
  - `ObterRelatorioVasilhamesQuery(PeriodoMes Periodo) : IQuery<RelatorioVasilhamesResult>`
  - `RelatorioVasilhamesResult(PeriodoMes Periodo, IReadOnlyList<VasilhamePorProduto> PorProduto, IReadOnlyList<EmprestimoEmAbertoItem> EmAberto, IReadOnlyList<DevolucaoDoDia> DevolucoesPorDia, int TotalEmprestadoNoMes, int TotalDevolvidoNoMes)`
  - `VasilhamePorProduto(Guid ProdutoId, string Nome, int EmprestadosNoMes, int DevolvidosNoMes, int EmAberto)`
  - `EmprestimoEmAbertoItem(Guid ClienteId, string ClienteNome, Guid ProdutoId, string ProdutoNome, int Saldo, DateTime DataMaisAntiga)`
  - `DevolucaoDoDia(int Dia, int Quantidade)`

- [ ] **Step 1: Write the failing tests**

| Teste | Cenário | Asserção exata |
|---|---|---|
| `Vasilhames_usa_historico_para_devolucoes_parciais_do_mes` | empréstimo de 5 (`DevolvidoEm == null`) + `HistoricoEstoque` tipo `DevolucaoVasilhame` qtd 2 no mês | `DevolvidosNoMes == 2` (mesmo sem `DevolvidoEm`) |
| `Vasilhames_lista_apenas_produtos_que_controlam_vasilhame` | 1 produto com `controlaVasilhame: true` e 1 sem | `PorProduto.Count == 1` |
| `Vasilhames_soma_em_aberto_por_cliente_e_produto` | 2 empréstimos abertos do mesmo cliente/produto (3 e 2) | `EmAberto.Single().Saldo == 5` |
| `Vasilhames_conta_emprestimos_do_mes` | empréstimo com `Data` no mês | `TotalEmprestadoNoMes == 5` |
| `Vasilhames_mes_sem_movimento_retorna_zerado` | sem empréstimo/devolução no mês | `TotalEmprestadoNoMes == 0`, `TotalDevolvidoNoMes == 0`, `DevolucoesPorDia.Count == DiasNoMes` |
| `Vasilhames_sem_empresa_selecionada_lanca` | tenant central | `InvalidOperationException` com "exige uma empresa" |

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RevendaApp.UnitTests --filter FullyQualifiedName~ObterRelatorioVasilhamesQueryHandlerTests`
Expected: FAIL.

- [ ] **Step 3: Implement query, handler and page**

- Handler: `EmpresaIdRequerida` primeiro; `EmprestimosVasilhames` com `DevolvidoEm == null`
  para o saldo em aberto (`Quantidade - QuantidadeDevolvida`, agrupado por cliente+produto, com
  `Data` mínima); empréstimos com `Data` no intervalo para `EmprestadosNoMes`; `HistoricoEstoque`
  com `Tipo == DevolucaoVasilhame` e `Data` no intervalo para `DevolvidosNoMes` e
  `DevolucoesPorDia`; só produtos com `ControlaVasilhame`.
- Página: `@page "/relatorios/vasilhames"`, `InteractiveServer`, `ApenasEmpresa`,
  `RelatorioShell`, cartões (empréstimos no mês, devoluções no mês, em aberto), tabela por produto,
  tabela "Com quem está" (cliente/produto/saldo) e gráfico de devoluções por dia.

- [ ] **Step 4: Run tests and build**

Run: `dotnet test tests/RevendaApp.UnitTests --filter FullyQualifiedName~ObterRelatorioVasilhamesQueryHandlerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RevendaApp.Web/Features/Relatorios/Vasilhames/ tests/RevendaApp.UnitTests/Features/Relatorios/ObterRelatorioVasilhamesQueryHandlerTests.cs
git commit -m "feat: adiciona relatorio de vasilhames"
```

---

# Fase 6 — isolamento entre empresas

### Task 15: `RelatoriosIsolationTests` com duas empresas

**Files:**
- Create: `tests/RevendaApp.IntegrationTests/RelatoriosIsolationTests.cs`
- Test: same file

**Interfaces:**
- Consumes: todos os handlers das Tasks 9, 12, 13, 14; `DatabaseFixture`, `EmpresaTestHelper`, `FakeTenantContext`.
- Produces: nada.

- [ ] **Step 1: Write the failing tests**

Estrutura da classe (mesmo padrão de `TenancyIsolationTests`: `IClassFixture<DatabaseFixture>`,
`IAsyncLifetime`, `ResetDatabaseAsync` no `InitializeAsync`, duas empresas criadas via
`CreateDbContextCentral()` + `EmpresaTestHelper`, dados de domínio sempre por
`CreateDbContext(empresaId)`).

Um teste por tabela lida, cada um com **controle positivo** (o dado aparece na própria empresa)
antes do assert de ausência:

```csharp
[Fact] public async Task RelatorioEstoque_de_A_nao_mostra_movimento_de_B()
[Fact] public async Task RelatorioVendas_de_A_nao_mostra_venda_de_B()
[Fact] public async Task RelatorioFiado_de_A_nao_lista_cliente_de_B()
[Fact] public async Task RelatorioFiado_de_A_nao_soma_recebimento_de_B()
[Fact] public async Task RelatorioVasilhames_de_A_nao_mostra_emprestimo_de_B()
[Fact] public async Task RelatorioVasilhames_de_A_nao_soma_devolucao_de_B()
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/RevendaApp.IntegrationTests --filter FullyQualifiedName~RelatoriosIsolationTests`
Expected: PASS ou FAIL dependendo de bug real; se passar de primeira, **verifique o controle
positivo** — um teste que não vê o dado da própria empresa é vacuoso. Corrija o cenário até o
controle positivo passar.

- [ ] **Step 3: Add the architecture test against `IgnoreQueryFilters`**

```csharp
// tests/RevendaApp.UnitTests/Architecture/RelatoriosSemIgnoreQueryFiltersTests.cs
[Fact]
public void Relatorios_nao_usam_IgnoreQueryFilters()
{
    // Lê os arquivos .cs de Features/Relatorios via Directory.GetFiles e falha se algum contém
    // "IgnoreQueryFilters" — a tentação existe ao montar agregações e ela anularia o filtro global.
}
```

- [ ] **Step 4: Run everything**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/RevendaApp.IntegrationTests/RelatoriosIsolationTests.cs tests/RevendaApp.UnitTests/Architecture/RelatoriosSemIgnoreQueryFiltersTests.cs
git commit -m "test: prova isolamento entre empresas nos relatorios"
```

---

### Task 16: Fechamento — formatação, build e verificação final

**Files:** nenhum arquivo novo.

- [ ] **Step 1: Format check**

Run: `dotnet format RevendaApp.slnx --no-restore --verify-no-changes`
Expected: sem alterações. Se houver, rode `dotnet format RevendaApp.slnx --no-restore` e commite
como `style: aplica dotnet format`.

- [ ] **Step 2: Full test run**

Run: `dotnet test`
Expected: PASS (unitários + integração).

- [ ] **Step 3: Confirm the branch has all commits and no stray files**

Run: `git log --oneline main..HEAD` e `git status --short`
Expected: 14 commits de feature na branch, working tree limpa.

- [ ] **Step 4: Report**

Informe ao usuário: comandos rodados, resultado de cada um, e qualquer teste que tenha sido
ajustado de contrato (as guardas de editar/excluir e o lucro do dashboard mudaram de
comportamento por design).