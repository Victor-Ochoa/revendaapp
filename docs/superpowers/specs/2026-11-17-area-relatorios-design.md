# Área de Relatórios — design

Data: 2026-11-17
Status: aguardando revisão

## 1. Objetivo

Criar a área **Relatórios** do Revenda.App, com uma landing que lista os relatórios
disponíveis e um padrão único (seletor de mês → query → tela) que os próximos relatórios
reaproveitam. A entrega começa por uma **correção de domínio no fiado** (Fase 0), porque o
relatório de fiado exporia um número errado sem ela.

| Entrega | Pergunta que responde | Fonte |
|---|---|---|
| **Fase 0 — quitação parcial de fiado** | Quanto cada venda fiado já foi paga e quanto falta | `Venda.ValorPagoFiado` (novo) |
| **Estoque por dia** | Quanto entrou e quanto saiu de cada produto, dia a dia no mês | `MovimentacaoEstoque` |
| **Vendas do mês** | Faturamento, lucro, nº de vendas, ticket e desconto por dia e por forma de pagamento | `Venda` + `ItemVenda` |
| **Fiado** | Quanto tenho a receber, de quem, há quanto tempo, e quanto recebi no mês | `Venda` (fiado), `Cliente.SaldoDevedor`, `Pagamento` |
| **Vasilhames** | Quantos vasilhames estão na rua, com quem, e quantos voltaram no mês | `EmprestimoVasilhame`, `HistoricoEstoque` |

Todos os relatórios são **somente leitura**, restritos à empresa do usuário (`ApenasEmpresa`) e
visíveis a qualquer usuário autenticado da empresa — mesmo critério de dashboards e listas.

## 2. Fora de escopo

- Exportação (CSV, impressão, PDF). Fica para quando o formato for acordado.
- Relatórios da central (super admin, cross-tenant). A área é de domínio, não da central.
- Reconstruir o **estoque físico** de um dia passado. Ver §4.1 — não é reconstruível com
  exatidão neste modelo e o relatório não finge que é.
- Alterar `Cliente.SaldoDevedor` durante a normalização. O saldo do cadastro é a autoridade de
  cobrança e não é reescrito; divergências são registradas, não silenciadas (§4.0.5).
- Edição/exclusão de venda passar a permitir fiado parcialmente pago: a guarda atual é
  reescrita para comparar dívida por venda (§4.0.4), mas a regra de negócio de o que pode ser
  editado não muda nesta entrega.

## 3. Navegação e rotas

| Rota | Conteúdo | Rendermode | Política |
|---|---|---|---|
| `/relatorios` | Landing com um cartão por relatório (título, descrição, ícone) | estático (SSR) | `ApenasEmpresa` |
| `/relatorios/estoque` | Estoque por dia | `InteractiveServer` | `ApenasEmpresa` |
| `/relatorios/vendas` | Vendas do mês | `InteractiveServer` | `ApenasEmpresa` |
| `/relatorios/fiado` | Fiado / inadimplência | `InteractiveServer` | `ApenasEmpresa` |
| `/relatorios/vasilhames` | Vasilhames | `InteractiveServer` | `ApenasEmpresa` |

- A landing é estática de propósito: usa apenas `MudPaper`/`MudText`/`MudButton Href`, que
  funcionam sem interatividade (mesma restrição documentada no `AGENTS.md` para páginas SSR).
- `MainLayout.razor` ganha um `MudButton Href="/relatorios"` "Relatórios" no `MudButtonGroup`
  existente (Home/Clientes/Estoque). **Não** é `MudMenu`: o layout não tem rendermode e um menu
  interativo exigiria extrair a navegação para um componente novo — mudança estrutural que não
  se justifica agora. Quando o menu de relatórios crescer, a landing absorve a navegação.
- `EstoqueLista.razor` ganha um botão secundário "Relatório do mês" no cabeçalho, levando a
  `/relatorios/estoque`.
- A landing só aparece no menu dentro de uma empresa (mesma condição `Identidade.TemEmpresa`
  já usada pelos outros links de domínio).

## 4. Decisões de domínio

### 4.0 Fase 0 — quitação parcial de fiado

#### 4.0.1 O defeito

`RegistrarPagamentoCommandHandler:60-67` aloca o pagamento em FIFO mas **tudo-ou-nada por
venda**:

```csharp
if (valorPago < venda.ValorTotal) break;   // parcial não aloca nada
venda.MarcarFiadoQuitado();
```

`Cliente.SaldoDevedor` é reduzido pelo valor pago inteiro, mas **nenhuma venda guarda quanto já
foi pago**. Um fiado de R$ 60 com R$ 50 pagos continua `FiadoAberto` valendo R$ 60. Consequências
medidas no código atual:

| Sintoma | Local |
|---|---|
| Lucro da venda nunca é reconhecido no dashboard, mesmo com 83% pago | `ObterDashboardQueryHandler:42-45` |
| Aging do relatório de fiado diria "R$ 60 em atraso" quando faltam R$ 10 | §6.3 (novo) |
| Guarda de editar/excluir compara `SaldoDevedor < venda.ValorTotal` — dívida total do cliente contra **uma** venda; bloqueia exclusão legítima e libera indevida | `EditarVendaCommandHandler:58`, `ExcluirVendaCommandHandler:47` |

#### 4.0.2 O modelo

`Venda` ganha `ValorPagoFiado` (`decimal`, default 0) e a regra de domínio passa a ser
**por venda**:

```csharp
public decimal ValorPagoFiado { get; private set; }        // quanto já foi alocado a esta venda
public decimal SaldoFiado => ValorTotal - ValorPagoFiado;   // quanto ainda falta nesta venda

/// Aloca um pagamento nesta venda (parcial ou total) e quita quando cobre o total.
public decimal AbaterFiado(decimal valor);                  // devolve o valor efetivamente alocado
```

Invariantes, provadas por teste de domínio:

- `AbaterFiado` só age em `FormaPagamento == Fiado` com `Status == FiadoAberto`; caso contrário
  devolve 0 (sem efeito, sem exceção — mesma tolerância de `MarcarFiadoQuitado`).
- Nunca aloca mais que `SaldoFiado`: o excedente fica com o chamador.
- `ValorPagoFiado` nunca ultrapassa `ValorTotal`; `Status` vira `FiadoQuitado` exatamente quando
  `SaldoFiado == 0`.
- Venda excluída (`ExcluidaEm != null`) não recebe alocação.

`MarcarFiadoQuitado()` **permanece** (usado pelo seed e por vendas legadas) e passa a ser
equivalente a alocar o saldo restante — assim não existem dois caminhos divergentes.

#### 4.0.3 Alocação FIFO no pagamento

`RegistrarPagamentoCommandHandler` troca o `break` por alocação parcial:

```
valorPago = command.Valor
para cada venda fiado aberta, por DataHora asc:
    alocado = venda.AbaterFiado(valorPago)
    valorPago -= alocado
    se valorPago == 0: break
```

- O FIFO continua estrito: só sobra para a venda seguinte o que a anterior não absorveu.
- Pagamento que excede a dívida total continua aceito (`Cliente.RegistrarPagamento` já faz clamp
  em zero); o excedente não é alocado a nenhuma venda e o comportamento atual de "crédito a
  favor" do cliente não muda nesta entrega.
- `Cliente.SaldoDevedor` continua sendo reduzido pelo valor pago **inteiro**, como hoje — é o
  total a receber, independente da alocação. A soma de `SaldoFiado` das vendas abertas é a
  visão por venda do mesmo número; divergência entre as duas é justamente o que a Fase 0
  corrige (o saldo do cliente era atualizado, o das vendas não).

#### 4.0.4 Guarda de editar/excluir

As duas guardas deixam de comparar o saldo total do cliente com o valor da venda e passam a
comparar **a própria venda**:

```csharp
// antes: cliente.SaldoDevedor < venda.ValorTotal
// depois:
venda.FormaPagamento == FormaPagamento.Fiado && venda.Status == VendaStatus.FiadoQuitado
```

Motivo: uma venda `FiadoQuitado` já teve seu lucro reconhecido; desfazê-la exige estorno do
pagamento, que o fluxo não faz. Uma venda `FiadoAberto` — mesmo parcialmente paga — pode ser
estornada, e `ReversaoVendaHelper` já abate `venda.ValorTotal` do saldo do cliente. **Atenção:**
com pagamento parcial o estorno hoje devolveria o total, não o que falta; o helper passa a abater
`venda.SaldoFiado` quando a venda já teve pagamento alocado. Isso entra na Fase 0 com teste
próprio.

#### 4.0.5 Migração e normalização dos dados legados

**Migração `ValorPagoFiadoNaVenda`:**

1. `ADD COLUMN ValorPagoFiado numeric NOT NULL DEFAULT 0`.
2. `UPDATE Vendas SET ValorPagoFiado = <ValorTotal>` para vendas `Fiado` com
   `Status = 'FiadoQuitado'` — quitadas antes desta coluna existir foram pagas integralmente,
   então o valor é exato e não precisa de reconstrução. `ValorTotal` é calculado
   (`ValorBruto − Desconto + ValorEntrega`) e não é coluna: o `UPDATE` soma os itens via
   `UPDATE ... FROM (SELECT VendaId, SUM(PrecoUnitario * Quantidade) ...)`. Coberto por teste
   de integração que roda o `Up` sobre dados montados.

**Normalização dos `FiadoAberto` legados (tarefa de bootstrap, não SQL na migração):**

A alocação parcial antiga **é recuperável**. `Pagamento.VendaId` distingue as duas naturezas:

| `Pagamento.VendaId` | Significado |
|---|---|
| `!= null` | pagamento no ato de venda não-fiado (não abate fiado) |
| `null` | abatimento de saldo devedor — **exatamente** o dinheiro que pagou fiado |

Logo, por cliente, a alocação correta é determinística:

```
vendas  = Vendas fiado, ExcluidaEm == null, ordenadas por DataHora asc
pagamentos = Pagamentos do cliente com VendaId == null, ordenados por Data asc
restante = 0
para cada pagamento (em ordem):
    restante += pagamento.Valor
    para cada venda ainda não quitada (na ordem):
        alocado = min(restante, Venda.SaldoFiado)
        Venda.ValorPagoFiado += alocado
        restante -= alocado
        se Venda.SaldoFiado == 0: Status = FiadoQuitado
```

Por que isso é seguro e não inventa dado:

- As duas fontes são **histórico imutável** (`Venda.DataHora`, `Venda.ValorTotal` com os itens,
  `Pagamento.Data`, `Pagamento.Valor`). Não há estimativa.
- Vendas excluídas ficam de fora — e `ReversaoVendaHelper` já abateu o `ValorTotal` do
  `SaldoDevedor` ao excluir, então o total continua fechando.
- É **idempotente**: recalcular sempre produz o mesmo resultado, porque só lê fatos gravados.
- O excedente (pagamento maior que a dívida, crédito a favor) não é alocado a nenhuma venda.

**Onde roda.** Serviço `NormalizarFiadoLegado` em `Features/Fiado/Normalizacao/`, chamado no
bootstrap do `Program.cs` **depois** de `MigrateAsync()` e antes de servir requisições — mesmo
lugar do `SeedData`/`SeedDemoData`. Não vai como SQL na migração porque a lógica é FIFO por
cliente, precisa ser testável e precisa deixar rastro; SQL de janela aqui seria ilegível e
impossível de cobrir com o nível de teste exigido pelo projeto.

- Roda em **qualquer ambiente**, inclusive Produção (é correção de dado, não demonstração).
- **Marcador de execução única:** grava um `RegistroAuditoria` global
  (`Acao = "FiadoLegadoNormalizado"`, `Origem = "Sistema"`, `EmpresaId = null`) com o resumo
  (`clientes analisados`, `vendas normalizadas`, `divergências`). Se o marcador existe, a tarefa
  não roda de novo. `RegistroAuditoria.Origens` ganha a constante `Sistema`.
- **Reconciliação:** ao final, compara `sum(Venda.SaldoFiado)` das vendas abertas com
  `Cliente.SaldoDevedor`. Divergência **não** é corrigida à força (o `SaldoDevedor` é a
  autoridade usada por cobrança e dashboard) — é registrada no `Detalhes` do marcador e no log
  de bootstrap, com o id do cliente. Divergência esperada só quando houve ajuste manual de
  saldo; qualquer outro caso indica bug e aparece no log.
- Falha na normalização **não derruba o startup**: é envolvida em try/catch que loga erro e
  segue. O app continua funcionando; o relatório de fiado marca os clientes afetados com
  `TemPagamentoNaoAlocado` (§4.3).
- Log de bootstrap: início, fim e o resumo — mesmo padrão de `LogsDoBootstrap`.

#### 4.0.6 Efeito no dashboard

Com `ValorPagoFiado`, o lucro reconhecido deixa de depender de "quitado por inteiro":

- `VendaLucro.Reconhecido` (§4.2) reconhece o lucro de fiado **proporcionalmente ao que foi
  pago**: `LucroTotal × (ValorPagoFiado / ValorTotal)`, e 100% quando `FiadoQuitado`.
- Venda não-fiado continua 100%.
- Isso muda números do dashboard — é a correção, não uma regressão. Os testes existentes de
  `ObterDashboardQueryHandlerTests` são atualizados com o caso novo (fiado pago pela metade
  reconhece metade do lucro) e mantêm os casos atuais (aberto = 0, quitado = 100%).

### 4.1 Estoque: movimento, não estoque físico

`MovimentacaoEstoque` registra `Entrada`, `Saida` e `Ajuste`. O `Ajuste` grava o **novo total**
em estoque, não o delta (`RegistrarMovimentacaoEstoqueCommandHandler:72` chama
`produto.AjustarEstoque(quantidade)`). Consequências, que o design respeita:

- `Ajuste` **não** entra na matriz de entradas/saídas. Misturá-lo corromperia os números.
- O saldo exibido é **movimento líquido acumulado do mês** (`entradas − saídas` desde o dia 1),
  nunca "estoque no fechamento do dia". O estoque físico de um dia passado não é reconstruível:
  ajustes gravam o total e não deixam o delta, e ajustes posteriores ao mês apagam o rastro.
- Para referência, a matriz mostra também **Estoque atual** (`Produto.QuantidadeEstoque`), que
  é estado de hoje e não do mês — rotulado como tal na tela.
- `Saida` de venda conta normalmente (o motivo é "Venda"): é exatamente o que se espera de
  "quanto saiu".
- A reversão de venda (`ReversaoVendaHelper`) grava `Entrada` e portanto aparece como entrada
  no dia — comportamento correto e já refletido no histórico do produto.

### 4.2 Vendas: reaproveitar a regra de lucro do dashboard

O lucro reconhecido exclui vendas fiado ainda em aberto e inclui fiado já quitado
(`ObterDashboardQueryHandler:42-45`). Duplicar essa regra no relatório criaria duas verdades.

- Extrair a regra para `Common/VendaLucro.cs` (`Reconhecido(IEnumerable<Venda>)`) e usá-la no
  handler do dashboard e no relatório. A regra já incorpora a proporcionalidade da Fase 0
  (§4.0.6). Os testes existentes do dashboard (`DashboardFeatureTests`,
  `ObterDashboardQueryHandlerTests`) são a rede de segurança da extração.
- `Venda.ValorTotal` e `LucroTotal` são propriedades calculadas sobre `Itens` (não são colunas):
  o handler precisa `Include(v => v.Itens)`, como o dashboard já faz.
- Vendas com `ExcluidaEm != null` ficam fora de tudo.

### 4.3 Fiado: aging pela dívida real de cada venda

- `Cliente.SaldoDevedor` é o total a receber. Com a Fase 0, a dívida por venda é
  `Venda.SaldoFiado` — o aging usa esse valor, não o `ValorTotal`.
- As faixas de atraso são calculadas a partir da **venda fiado mais antiga com
  `Status == FiadoAberto`** por cliente. Uma venda parcialmente paga e ainda aberta entra pelo
  seu saldo real.
- O legado é **normalizado** (§4.0.5), então não há limitação de aging por dado ausente. O
  `TemPagamentoNaoAlocado` continua existindo como **detector de divergência**: se a
  reconciliação do bootstrap registrou que `sum(SaldoFiado) != Cliente.SaldoDevedor` para o
  cliente, a linha vem marcada e a tela mostra "saldo do cadastro difere das vendas".
- Faixas: 0–30, 31–60, 61–90, 90+ dias.
- Recebimentos do mês vêm de `Pagamento.Data` no intervalo, com total por forma de pagamento.

### 4.4 Vasilhames: estado atual + devoluções do mês

- **Saldo em aberto** (estado, não período) vem de `EmprestimoVasilhame` com
  `DevolvidoEm == null`, agregado por cliente × produto (`Quantidade − QuantidadeDevolvida`).
- **Devoluções do mês** vêm de `HistoricoEstoque` com `Tipo == DevolucaoVasilhame` e `Data` no
  intervalo, somando `Quantidade`. `EmprestimoVasilhame` **não** serve para isso: `DevolvidoEm`
  só é preenchido na quitação total, então devoluções parciais não teriam data.
- **Empréstimos feitos no mês** vêm de `EmprestimoVasilhame.Data` no intervalo (inclui os
  originados por venda e os manuais).

## 5. Peças compartilhadas

### 5.1 `PeriodoMes`

`RevendaApp.Web/Common/Relatorios/PeriodoMes.cs`:

```csharp
public readonly record struct PeriodoMes(int Ano, int Mes)
{
    public DateTime InicioUtc { get; }       // BrasilTimeZone.ParaUtc(dia 1 00:00)
    public DateTime FimUtc { get; }          // BrasilTimeZone.ParaUtc(dia 1 do mês seguinte) — exclusivo
    public int DiasNoMes { get; }            // 28/29/30/31
    public string Rotulo { get; }            // "novembro/2026"
    public static PeriodoMes DoDia(DateTime brasilia);   // mês civil de Brasília
    public PeriodoMes Anterior();
    public PeriodoMes Proximo();
}
```

Fonte única do intervalo UTC de um mês: nenhum handler monta intervalo na mão. Validar
`Ano`/`Mes` no construtor (mês 1..12) — o valor vem da UI, mas o handler não confia na UI.

### 5.2 `RelatorioShell.razor`

Componente de apresentação (`Features/Relatorios/Shell/RelatorioShell.razor`) com:

- `Titulo`, `Descricao`;
- seletor de mês/ano (dois `MudSelect`: mês corrente de Brasília por padrão; ano com os
  últimos 5 anos);
- `ChildContent` (o corpo do relatório) e `Rodape` opcional;
- callback `OnMesAlterado` (o filho recarrega a própria query);
- estado de carregamento e de "sem dados" padronizados.

O componente é usado dentro de páginas interativas, então **não** declara `@rendermode`.

### 5.3 Landing

`Features/Relatorios/Home/RelatoriosHome.razor` — grade de cartões (`MudGrid`), cada cartão
com título, uma frase do que responde e link. Lista os quatro relatórios desta entrega; os
próximos entram como novo cartão + rota.

## 6. Contratos das queries

Todos em `Features/Relatorios/<Relatorio>/`, seguindo VSA (query + handler + página no mesmo
diretório). **Primeira instrução de todo handler:** `_ = tenant.EmpresaIdRequerida("<ação>")`.

### 6.1 Estoque

```csharp
ObterRelatorioEstoqueQuery(PeriodoMes Periodo) : IQuery<RelatorioEstoqueResult>

RelatorioEstoqueResult(
    PeriodoMes Periodo,
    IReadOnlyList<ProdutoNoMes> Produtos,
    IReadOnlyList<AjusteNoMes> Ajustes,
    IReadOnlyList<MovimentoDoDia> PorDia);      // total da empresa por dia (gráfico)

ProdutoNoMes(
    Guid ProdutoId, string Nome, bool Ativo,
    IReadOnlyList<int> EntradasPorDia,          // índice 0 = dia 1
    IReadOnlyList<int> SaidasPorDia,
    int TotalEntradas, int TotalSaidas, int MovimentoLiquido,
    int EstoqueAtual, int QuantidadeAjustes);

AjusteNoMes(Guid ProdutoId, string ProdutoNome, int Dia, int NovoTotal, string? Motivo);

MovimentoDoDia(int Dia, int Entradas, int Saidas);
```

Regras:

- Intervalo `[Periodo.InicioUtc, Periodo.FimUtc)`. Agrupamento pelo **dia civil de Brasília**
  (`BrasilTimeZone.ParaBrasilia` após materializar — o EF não traduz `TimeZoneInfo`).
- Produtos na matriz: **todos** os produtos da empresa, inclusive inativos, ordenados por nome
  — mesmo critério da tela de estoque, porque um produto desativado no mês corrente ainda tem
  histórico a explicar em meses passados. Inativo vem com `Ativo = false` e chip "Inativo".
- Produto sem movimento no mês aparece com vetores zerados.
- `Ajustes` lista cada `MovimentacaoEstoque` do tipo `Ajuste` do mês.

### 6.2 Vendas

```csharp
ObterRelatorioVendasQuery(PeriodoMes Periodo) : IQuery<RelatorioVendasResult>

RelatorioVendasResult(
    PeriodoMes Periodo,
    IReadOnlyList<VendaDoDia> PorDia,
    IReadOnlyList<VendaPorFormaNoMes> PorForma,
    decimal FaturamentoTotal, decimal LucroTotal, decimal DescontoTotal,
    int QuantidadeVendas, decimal TicketMedio)

VendaDoDia(int Dia, int Quantidade, decimal Faturamento, decimal Lucro, decimal Desconto);

VendaPorFormaNoMes(FormaPagamento Forma, int Quantidade, decimal Valor);
```

- `TicketMedio = FaturamentoTotal / QuantidadeVendas` (0 quando não há venda).
- Lucro pela regra compartilhada (§4.2).

### 6.3 Fiado

```csharp
ObterRelatorioFiadoQuery(PeriodoMes Periodo) : IQuery<RelatorioFiadoResult>

RelatorioFiadoResult(
    PeriodoMes Periodo,
    decimal TotalAReceber,
    IReadOnlyList<FaixaAging> Faixas,
    IReadOnlyList<ClienteDevedorItem> Clientes,
    IReadOnlyList<RecebimentoDoDia> RecebimentosPorDia,
    IReadOnlyList<RecebimentoPorForma> RecebimentosPorForma,
    decimal TotalRecebido)

FaixaAging(string Rotulo, int Clientes, decimal Valor);   // 0–30, 31–60, 61–90, 90+

ClienteDevedorItem(
    Guid ClienteId, string Nome, string Telefone,
    decimal Saldo, int DiasAtraso, DateTime? VendaMaisAntigaEmAberto,
    bool TemDivergenciaDeSaldo);       // §4.3: sum(SaldoFiado) != SaldoDevedor do cadastro
```

- Clientes com `SaldoDevedor > 0`, ordenados por saldo desc.
- `DiasAtraso` = dias de calendário (Brasília) desde a venda fiado em aberto mais antiga.
- `RecebimentosPorDia`/`PorForma` do mês a partir de `Pagamento`.

### 6.4 Vasilhames

```csharp
ObterRelatorioVasilhamesQuery(PeriodoMes Periodo) : IQuery<RelatorioVasilhamesResult>

RelatorioVasilhamesResult(
    PeriodoMes Periodo,
    IReadOnlyList<VasilhamePorProduto> PorProduto,
    IReadOnlyList<EmprestimoEmAbertoItem> EmAberto,
    IReadOnlyList<DevolucaoDoDia> DevolucoesPorDia,
    int TotalEmprestadoNoMes, int TotalDevolvidoNoMes)

VasilhamePorProduto(
    Guid ProdutoId, string Nome,
    int EmprestadosNoMes, int DevolvidosNoMes, int EmAberto);

EmprestimoEmAbertoItem(
    Guid ClienteId, string ClienteNome, Guid ProdutoId, string ProdutoNome,
    int Saldo, DateTime DataMaisAntiga);

DevolucaoDoDia(int Dia, int Quantidade);
```

- Só produtos com `ControlaVasilhame` entram em `PorProduto`.
- `EmAberto` por produto soma os empréstimos não quitados.

## 7. Persistência

### 7.1 Fase 0 — `ValorPagoFiadoNaVenda`

Coluna nova em `Vendas` + backfill (§4.0.5). Sem índice novo: a consulta de fiado em aberto já
usa `IX_Vendas_EmpresaId_DataHora` e o filtro por `Status`.

### 7.2 Fase 1 — `IndicesRelatorios`

Nenhuma coluna nova; só índices:

| Índice | Motivo |
|---|---|
| `MovimentacoesEstoque (EmpresaId, Data)` | varredura do mês no relatório de estoque |
| `HistoricosEstoque (EmpresaId, Data)` | devoluções do mês no relatório de vasilhames |
| `EmprestimosVasilhames (EmpresaId, DevolvidoEm)` | saldo em aberto por empresa |

Já existem e são reaproveitados: `Vendas (EmpresaId, DataHora)`,
`Pagamentos (EmpresaId, Data)`, `EmprestimosVasilhames (ClienteId, ProdutoId)`.
Os índices simples em `EmpresaId` dessas três tabelas são **substituídos** pelos compostos
(padrão da migração `IndicesDashboard`).

## 8. Testes

TDD: cada item é escrito antes da implementação correspondente.

### Fase 0 — fiado

| Teste | Tipo | Pina |
|---|---|---|
| `AbaterFiado_aloca_parcialmente_e_mantem_venda_aberta` | unit (domínio) | §4.0.2 |
| `AbaterFiado_quita_quando_alocacao_cobre_o_total` | unit (domínio) | `SaldoFiado == 0` → `FiadoQuitado` |
| `AbaterFiado_nao_aloca_alem_do_saldo_e_devolve_o_excedente` | unit (domínio) | limite superior |
| `AbaterFiado_ignora_venda_que_nao_e_fiado_ou_ja_quitada` | unit (domínio) | tolerância |
| `AbaterFiado_nao_altera_venda_excluida` | unit (domínio) | soft delete |
| `Pagamento_parcial_reduz_o_saldo_da_venda_mais_antiga_sem_quitar` | unit (handler) | substitui o teste atual `NaoDeveMarcarVendaQuitada_QuandoPagamentoForParcial`, agora com `ValorPagoFiado == 40` |
| `Pagamento_que_cobre_duas_vendas_quita_a_antiga_e_abate_a_seguinte` | unit (handler) | FIFO com alocação parcial |
| `Pagamento_maior_que_a_divida_nao_aloca_excedente` | unit (handler) | §4.0.3 |
| `Reversao_de_venda_parcialmente_paga_abate_apenas_o_saldo_fiado` | unit (helper) | §4.0.4 |
| `Edicao_de_venda_fiado_aberta_parcialmente_paga_e_permitida` | unit (handler) | guarda reescrita |
| `Edicao_de_venda_fiado_quitada_e_bloqueada` | unit (handler) | guarda reescrita |
| `Backfill_marca_vendas_fiado_quitadas_com_valor_total` | integração (migração) | §4.0.5 passo 2 |
| `Normalizacao_aloca_pagamentos_parciais_nas_vendas_mais_antigas` | unit | FIFO a partir de `Pagamento.VendaId == null` |
| `Normalizacao_quita_venda_quando_a_alocacao_cobre_o_total` | unit | status |
| `Normalizacao_ignora_pagamento_de_venda_no_ato` | unit | `VendaId != null` não abate fiado |
| `Normalizacao_ignora_venda_excluida` | unit | soft delete |
| `Normalizacao_e_idempotente` | unit | rodar duas vezes dá o mesmo resultado |
| `Normalizacao_nao_aloca_excedente_de_credito` | unit | pagamento > dívida |
| `Normalizacao_nao_roda_quando_o_marcador_ja_existe` | unit | execução única |
| `Normalizacao_registra_divergencia_sem_alterar_saldo_do_cliente` | unit | reconciliação |
| `Normalizacao_falha_nao_derruba_o_bootstrap` | unit | try/catch |
| `Normalizacao_corrige_banco_legado_contra_Postgres` | integração | cenário real ponta a ponta |

### Relatórios — unitários (InMemory)

| Teste | Pina |
|---|---|
| `PeriodoMes_Dezembro_vira_janeiro_ao_avancar` | virada de ano |
| `PeriodoMes_calcula_intervalo_utc_do_mes_de_brasilia` | 1º dia 03:00 UTC; fim exclusivo |
| `PeriodoMes_calcula_dias_do_mes_inclusive_fevereiro_bissexto` | 28/29/30/31 |
| `PeriodoMes_rejeita_mes_fora_de_1_a_12` | validação do valor vindo da UI |
| `Estoque_agrupa_entradas_e_saidas_por_dia_de_brasilia` | movimento às 02:00 UTC cai no dia anterior (23:00 BRT) |
| `Estoque_exclui_ajustes_da_matriz_e_lista_em_ajustes` | §4.1 |
| `Estoque_inclui_inativo_com_movimento_marcado_como_inativo` | §6.1 |
| `Estoque_inclui_inativo_sem_movimento_com_vetores_zerados` | §6.1 |
| `Estoque_mes_sem_movimento_retorna_vetores_zerados` | estado vazio |
| `Estoque_calcula_totais_liquido_e_estoque_atual` | totais |
| `Vendas_exclui_venda_excluida_logicamente` | soft delete |
| `Vendas_reconhece_lucro_proporcional_ao_pago_no_fiado` | §4.0.6 |
| `Vendas_calcula_ticket_medio_e_zero_sem_vendas` | divisão por zero |
| `Fiado_usa_saldo_fiado_e_nao_o_valor_total_no_aging` | §4.3 |
| `Fiado_marca_aviso_quando_saldo_do_cliente_e_menor_que_a_soma_das_vendas` | §4.3 legado |
| `Fiado_soma_recebimentos_do_mes_por_forma` | pagamentos |
| `Vasilhames_usa_historico_para_devolucoes_parciais_do_mes` | §4.4 |
| `Vasilhames_lista_apenas_produtos_que_controlam_vasilhame` | filtro |
| `Relatorios_sem_empresa_selecionada_lancam` (um teste por handler) | guarda de tenant |

### Relatórios — integração (`tests/RevendaApp.IntegrationTests`, `DatabaseFixture`)

| Teste | Pina |
|---|---|
| `RelatorioEstoque_agrega_movimentos_do_mes_contra_Postgres` | agrupamento e intervalo reais |
| `RelatorioVendas_bate_com_dashboard_no_mesmo_periodo` | a regra de lucro não divergiu |
| `RelatorioFiado_aging_e_recebimentos_contra_Postgres` | aging + pagamento real |
| `RelatorioVasilhames_devolucao_parcial_aparece_no_mes` | §4.4 |

Cada classe chama `await fixture.ResetDatabaseAsync()` no `InitializeAsync`.

### 8.1 Isolamento entre empresas (obrigatório, não opcional)

O `AGENTS.md` trava a lista de entidades filtradas por `FiltrosGlobaisTests`; todo relatório novo
é uma **leitura nova** e precisa de prova de isolamento contra o Postgres real, com **duas
empresas e controle positivo** — mesmo padrão de `TenancyIsolationTests` (o assert de ausência só
vale junto com o de presença na própria empresa).

Os quatro relatórios leem cinco tabelas: `MovimentacaoEstoque`, `Venda`, `ItemVenda`,
`Pagamento`, `EmprestimoVasilhame`, `HistoricoEstoque` e `Produto`. A matriz de cobertura:

| Tabela | Filtro que a protege | Teste de isolamento |
|---|---|---|
| `MovimentacaoEstoque` | global (`EmpresaId`) | `RelatorioEstoque_de_A_nao_mostra_movimento_de_B` |
| `Produto` | global | coberto pelo teste acima (nome/estoque do produto de B ausente) |
| `Venda` / `ItemVenda` | global | `RelatorioVendas_de_A_nao_mostra_venda_de_B` |
| `Pagamento` | global | `RelatorioFiado_de_A_nao_soma_recebimento_de_B` |
| `EmprestimoVasilhame` | global | `RelatorioVasilhames_de_A_nao_mostra_emprestimo_de_B` |
| `HistoricoEstoque` | global | `RelatorioVasilhames_de_A_nao_soma_devolucao_de_B` |
| `Cliente` (saldo) | global | `RelatorioFiado_de_A_nao_lista_cliente_de_B` |
| `Empresa` (identidade) | **sem filtro** (por design) | não é lida pelos relatórios |

Regras de implementação que os testes cobrem:

- Todo handler começa por `tenant.EmpresaIdRequerida(...)` — em modo central **lança**, não
  devolve "todas as empresas" (um teste por handler, §8 unitários).
- Nenhum handler usa `IgnoreQueryFilters()`. Isso é verificado por teste de arquitetura
  (`Relatorios_nao_usam_IgnoreQueryFilters`), que falha se a chamada aparecer no código dos
  relatórios — a tentação existe justamente ao montar agregações.
- Nenhum relatório aceita `EmpresaId` como parâmetro vindo da UI: o tenant vem só do
  `ITenantContext`. A query carrega apenas `PeriodoMes`.
- Os testes de isolamento vivem em `RelatoriosIsolationTests` (classe própria, no padrão de
  `TenancyIsolationTests`), com `EmpresaTestHelper` e `CreateDbContext(Guid?)` — dados de cada
  empresa criados por um contexto **com o tenant dela**, nunca pelo central.

### Componentes

`RelatoriosHome` lista os quatro relatórios com link correto (teste de markup, no padrão de
`EstoqueListaAdminTests`).

## 9. Fases de execução — **uma única entrega (um MR)**

Cada deploy é único: a entrega é **um MR só**, com as fases como **commits encadeados na mesma
branch**, não como MRs separados. Cada fase termina com build, `dotnet test` e
`dotnet format --verify-no-changes` verdes antes do commit seguinte.

| # | Commit | Conteúdo |
|---|---|---|
| 0 | `feat: registra quitacao parcial de fiado por venda` | `Venda.ValorPagoFiado` + `AbaterFiado`, alocação FIFO parcial, guardas de editar/excluir, `ReversaoVendaHelper`, `VendaLucro` proporcional, migração |
| 1 | `feat: normaliza quitacao de fiado legado no bootstrap` | `NormalizarFiadoLegado`, marcador de execução única, reconciliação, log de bootstrap |
| 2 | `feat: adiciona area de relatorios com estoque por dia` | `PeriodoMes`, `RelatorioShell`, landing `/relatorios`, botões de navegação, relatório de estoque, migração de índices |
| 3 | `feat: adiciona relatorio de vendas do mes` | consome `VendaLucro` |
| 4 | `feat: adiciona relatorio de fiado` | consome `SaldoFiado` normalizado |
| 5 | `feat: adiciona relatorio de vasilhames` | — |

Por que a ordem importa dentro do mesmo MR: as fases 3 e 4 só exibem número correto porque as
fases 0 e 1 já corrigiram e normalizaram o fiado. Se a entrega for interrompida no meio, a
ordem dos commits deixa o repositório coerente em cada ponto — cada commit compila e passa nos
testes.

Critério de pronto do MR:

- `dotnet build` sem warnings novos.
- `dotnet test` completo verde (unitários + integração).
- `dotnet format RevendaApp.slnx --no-restore --verify-no-changes` limpo.
- `RelatoriosIsolationTests` verde (§8.1).
- Os quatro relatórios acessíveis por `/relatorios` e pelo menu.
- Normalização do fiado verificada contra um banco com dados legados (§8, teste de integração
  `Normalizacao_corrige_banco_legado_contra_Postgres`).

## 10. Riscos e limitações assumidas

| Risco | Mitigação |
|---|---|
| Normalização do legado altera dados de produção na primeira subida | deriva só de histórico imutável (`Pagamento.VendaId == null`, `Venda.DataHora`, `ValorTotal`), é idempotente, roda uma vez com marcador em `RegistroAuditoria` e deixa resumo + divergências no log; falha não derruba o startup |
| Normalização pode divergir do `SaldoDevedor` do cadastro | reconciliação registra a divergência e **não** sobrescreve o saldo; o relatório marca o cliente (`TemDivergenciaDeSaldo`) |
| A Fase 0 **muda números do dashboard** (lucro de fiado parcialmente pago passa a ser reconhecido) | é a correção; testes do dashboard atualizados no mesmo MR, com caso novo explícito |
| Estorno de venda parcialmente paga precisa devolver só o saldo, não o total | `ReversaoVendaHelper` ajustado e coberto por teste na Fase 0 |
| Relatório de estoque não representa estoque físico de dias passados | rótulo explícito "movimento do mês" + "estoque atual" separado |
| Mês inteiro de movimentos materializado em memória | volume esperado é baixo por empresa; índice composto e projeção mínima; se crescer, agregação no SQL com offset fixo é o próximo passo |
| `MainLayout` sem rendermode limita o menu a botões simples | a landing absorve a navegação; dropdown exige componente interativo e fica para quando houver demanda |
| Relatório novo esquecer o filtro de empresa | `RelatoriosIsolationTests` com duas empresas e controle positivo (§8.1) + teste que proíbe `IgnoreQueryFilters` nos relatórios |