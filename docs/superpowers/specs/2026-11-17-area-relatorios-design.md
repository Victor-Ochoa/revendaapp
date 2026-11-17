# Área de Relatórios — design

Data: 2026-11-17
Status: aguardando revisão

## 1. Objetivo

Criar a área **Relatórios** do Revenda.App, com uma landing que lista os relatórios
disponíveis e um padrão único (seletor de mês → query → tela) que os próximos relatórios
reaproveitam. Esta entrega inclui quatro relatórios:

| Relatório | Pergunta que responde | Fonte |
|---|---|---|
| **Estoque por dia** | Quanto entrou e quanto saiu de cada produto, dia a dia no mês | `MovimentacaoEstoque` |
| **Vendas do mês** | Faturamento, lucro, nº de vendas, ticket e desconto por dia e por forma de pagamento | `Venda` + `ItemVenda` |
| **Fiado** | Quanto tenho a receber, de quem, há quanto tempo, e quanto recebi no mês | `Cliente.SaldoDevedor`, `Venda` (fiado aberto), `Pagamento` |
| **Vasilhames** | Quantos vasilhames estão na rua, com quem, e quantos voltaram no mês | `EmprestimoVasilhame`, `HistoricoEstoque` |

Todos são **somente leitura**, restritos à empresa do usuário (`ApenasEmpresa`) e visíveis a
qualquer usuário autenticado da empresa — mesmo critério de dashboards e listas.

## 2. Fora de escopo

- Exportação (CSV, impressão, PDF). Fica para quando o formato for acordado.
- Relatórios da central (super admin, cross-tenant). A área é de domínio, não da central.
- Reconstruir o **estoque físico** de um dia passado. Ver §4.1 — não é reconstruível com
  exatidão neste modelo e o relatório não finge que é.
- Alterar as regras de lucro/fiado. O relatório de vendas consome a mesma regra do dashboard
  (ver §4.2).

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
  handler do dashboard e no relatório. Os testes existentes do dashboard (`DashboardFeatureTests`)
  são a rede de segurança da extração.
- `Venda.ValorTotal` e `LucroTotal` são propriedades calculadas sobre `Itens` (não são colunas):
  o handler precisa `Include(v => v.Itens)`, como o dashboard já faz.
- Vendas com `ExcluidaEm != null` ficam fora de tudo.

### 4.3 Fiado: aging pelo fiado mais antigo em aberto

- `Cliente.SaldoDevedor` é o total a receber. As faixas de atraso são calculadas a partir da
  **venda fiado mais antiga ainda com `Status == FiadoAberto`** por cliente.
- Limitação conhecida, herdada do modelo: `RegistrarPagamentoCommandHandler` aloca o pagamento
  tudo-ou-nada por venda e para no primeiro que não cobrir o total. Um pagamento parcial deixa
  a venda antiga aberta, então o aging pode indicar atraso maior que o real. Isso é documentado
  na tela (tooltip) e no XML doc do handler — o relatório não inventa uma idade que o modelo
  não sabe.
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
    decimal Saldo, int DiasAtraso, DateTime? VendaMaisAntigaEmAberto);
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

Uma migração `IndicesRelatorios` (nenhuma coluna nova):

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

### Unitários (`tests/RevendaApp.UnitTests`, InMemory)

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
| `Vendas_nao_reconhece_lucro_de_fiado_em_aberto` | §4.2 |
| `Vendas_calcula_ticket_medio_e_zero_sem_vendas` | divisão por zero |
| `Fiado_calcula_faixas_de_atraso_pela_venda_mais_antiga_em_aberto` | §4.3 |
| `Fiado_soma_recebimentos_do_mes_por_forma` | pagamentos |
| `Vasilhames_usa_historico_para_devolucoes_parciais_do_mes` | §4.4 |
| `Vasilhames_lista_apenas_produtos_que_controlam_vasilhame` | filtro |
| `Relatorios_sem_empresa_selecionada_lancam` (um teste por handler) | guarda de tenant |

### Integração (`tests/RevendaApp.IntegrationTests`, `DatabaseFixture`)

| Teste | Pina |
|---|---|
| `RelatorioEstoque_agrega_movimentos_do_mes_contra_Postgres` | agrupamento e intervalo reais |
| `RelatorioEstoque_nao_ve_movimento_de_outra_empresa` | isolamento |
| `RelatorioVendas_bate_com_dashboard_no_mesmo_periodo` | a regra de lucro não divergiu |
| `RelatorioFiado_aging_e_recebimentos_contra_Postgres` | aging + pagamento real |
| `RelatorioVasilhames_devolucao_parcial_aparece_no_mes` | §4.4 |

Cada classe chama `await fixture.ResetDatabaseAsync()` no `InitializeAsync`.

### Componentes

`RelatoriosHome` lista os quatro relatórios com link correto (teste de markup, no padrão de
`EstoqueListaAdminTests`).

## 9. Fases de entrega

Cada fase termina com build, `dotnet test` e `dotnet format --verify-no-changes` verdes, e um
commit próprio (Conventional Commits).

| Fase | Conteúdo | MR |
|---|---|---|
| 1 | `PeriodoMes`, `RelatorioShell`, landing `/relatorios`, botão no `MainLayout` e em `EstoqueLista`, relatório de **estoque**, migração de índices | MR #1 |
| 2 | Relatório de **vendas** + extração de `VendaLucro` (dashboard passa a usar) | MR #2 |
| 3 | Relatório de **fiado** | MR #3 |
| 4 | Relatório de **vasilhames** | MR #4 |

A fase 1 sozinha já entrega o pedido original. As demais são incrementais e independentes entre
si, então podem ser reordenadas ou interrompidas sem retrabalho.

## 10. Riscos e limitações assumidas

| Risco | Mitigação |
|---|---|
| Aging de fiado pode superestimar atraso (pagamento parcial não quita venda) | documentado em tooltip e no XML doc; corrigir o modelo é outra feature |
| Relatório de estoque não representa estoque físico de dias passados | rótulo explícito "movimento do mês" + "estoque atual" separado |
| Mês inteiro de movimentos materializado em memória | volume esperado é baixo por empresa; índice composto e projeção mínima; se crescer, agregação no SQL com offset fixo é o próximo passo |
| `MainLayout` sem rendermode limita o menu a botões simples | a landing absorve a navegação; dropdown exige componente interativo e fica para quando houver demanda |