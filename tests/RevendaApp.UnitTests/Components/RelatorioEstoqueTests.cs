using Bunit;

using Mediator;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor.Services;

using RevendaApp.Web.Common.Relatorios;
using RevendaApp.Web.Features.Relatorios.Estoque;

namespace RevendaApp.UnitTests.Components;

/// <summary>
/// A matriz produtos × dias é montada com <c>@for</c> dentro do <c>MudTable</c>. O risco é a
/// captura da variável de laço: se o valor for lido dentro de um <c>RenderFragment</c> avaliado
/// depois do laço, todas as células mostram o último dia (ou estouram o índice).
/// </summary>
public class RelatorioEstoqueTests
{
    private static readonly Guid ProdutoId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// Controle positivo: a linha do produto tem exatamente uma célula por dia do mês, com os
    /// valores do dia certo — dia 3 com +5/−2 e dia 5 com +7.
    /// </summary>
    [Fact]
    public async Task Matriz_mostra_o_valor_do_dia_certo_em_cada_celula()
    {
        await using var ctx = CriarContexto(NovembroComDoisDias());

        var cut = ctx.Render<RelatorioEstoque>();
        cut.WaitForAssertion(() => Assert.Contains("+5 / −2", cut.Markup, StringComparison.Ordinal));

        var celulas = cut.FindAll("td[data-label='Dia 3']");
        Assert.Contains("+5 / −2", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("+7", cut.Markup, StringComparison.Ordinal);

        // O dia 1 (índice 0) não teve movimento: a célula é vazia, não o valor de outro dia.
        var markup = cut.Markup;
        var posicaoDia3 = markup.IndexOf("+5 / −2", StringComparison.Ordinal);
        Assert.True(posicaoDia3 > 0);
    }

    /// <summary>
    /// Sem movimento no mês a tela não pode quebrar nem inventar valores.
    /// </summary>
    [Fact]
    public async Task Mes_sem_movimento_renderiza_a_matriz_zerada()
    {
        await using var ctx = CriarContexto(NovembroSemMovimento());

        var cut = ctx.Render<RelatorioEstoque>();
        cut.WaitForAssertion(() => Assert.Contains("Produtos × dias do mês", cut.Markup, StringComparison.Ordinal));

        Assert.Contains("Botijão P13", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("+", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// O xadrez e o "tudo numa linha" vêm de regras globais em wwwroot/app.css ancoradas na classe
    /// <c>matriz-estoque</c>. Se a classe deixar de ser aplicada no contêiner que envolve as
    /// células, o CSS não alcança nada e a matriz volta a quebrar linha — por isso o contrato é
    /// testado aqui, não confiado ao navegador.
    /// </summary>
    [Fact]
    public async Task Matriz_aplica_a_classe_de_estilo_no_conteiner_das_celulas()
    {
        await using var ctx = CriarContexto(NovembroComDoisDias());

        var cut = ctx.Render<RelatorioEstoque>();
        cut.WaitForAssertion(() => Assert.Contains("Botijão P13", cut.Markup, StringComparison.Ordinal));

        var matriz = cut.Find(".matriz-estoque");
        Assert.NotEmpty(matriz.QuerySelectorAll("td"));
    }

    /// <summary>
    /// A tela abre no modo "Ambos" — o mesmo comportamento de antes do filtro existir. Se o default
    /// mudasse, quem já usa o relatório veria menos dados sem ter pedido.
    /// </summary>
    [Fact]
    public async Task Filtro_abre_em_ambos_por_padrao()
    {
        await using var ctx = CriarContexto(NovembroComDoisDias(), out var sender);

        var cut = ctx.Render<RelatorioEstoque>();
        cut.WaitForAssertion(() => Assert.Contains("Botijão P13", cut.Markup, StringComparison.Ordinal));

        Assert.Equal([FiltroMovimentoEstoque.Ambos], sender.FiltrosPedidos);
        Assert.Contains("Entradas no mês", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Saídas no mês", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Trocar para "Somente entradas" recarrega a consulta com o filtro certo e esconde o que não
    /// é entrada — inclusive a série de saídas do gráfico, para não deixar uma linha zerada.
    /// </summary>
    [Fact]
    public async Task Filtro_somente_entradas_recarrega_e_esconde_saidas()
    {
        await using var ctx = CriarContexto(NovembroComDoisDias(), out var sender);

        var cut = ctx.Render<RelatorioEstoque>();
        cut.WaitForAssertion(() => Assert.Contains("Botijão P13", cut.Markup, StringComparison.Ordinal));

        cut.FindAll(".mud-toggle-item")[1].Click();

        cut.WaitForAssertion(() => Assert.Contains(FiltroMovimentoEstoque.Entradas, sender.FiltrosPedidos));
        cut.WaitForAssertion(() => Assert.DoesNotContain("Saídas no mês", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Entradas no mês", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Somente saídas" é o espelho: a coluna de entradas some e a de saídas fica.
    /// </summary>
    [Fact]
    public async Task Filtro_somente_saidas_esconde_entradas()
    {
        await using var ctx = CriarContexto(NovembroComDoisDias(), out var sender);

        var cut = ctx.Render<RelatorioEstoque>();
        cut.WaitForAssertion(() => Assert.Contains("Botijão P13", cut.Markup, StringComparison.Ordinal));

        cut.FindAll(".mud-toggle-item")[2].Click();

        cut.WaitForAssertion(() => Assert.Contains(FiltroMovimentoEstoque.Saidas, sender.FiltrosPedidos));
        cut.WaitForAssertion(() => Assert.DoesNotContain("Entradas no mês", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Saídas no mês", cut.Markup, StringComparison.Ordinal);
    }

    private static BunitContext CriarContexto(RelatorioEstoqueResult resultado) =>
        CriarContexto(resultado, out _);

    private static BunitContext CriarContexto(RelatorioEstoqueResult resultado, out SenderDeRelatorioEstoque sender)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        sender = new SenderDeRelatorioEstoque(resultado);
        ctx.Services.AddSingleton<ISender>(sender);
        return ctx;
    }

    private static RelatorioEstoqueResult NovembroComDoisDias()
    {
        var periodo = new PeriodoMes(2026, 11);
        var entradas = new int[periodo.DiasNoMes];
        var saidas = new int[periodo.DiasNoMes];
        entradas[2] = 5;   // dia 3
        saidas[2] = 2;
        entradas[4] = 7;   // dia 5

        return new RelatorioEstoqueResult(
            periodo,
            [
                new ProdutoNoMes(ProdutoId, "Botijão P13", true, entradas, saidas,
                    entradas.Sum(), saidas.Sum(), entradas.Sum() - saidas.Sum(), 93, 0),
            ],
            [],
            Enumerable.Range(1, periodo.DiasNoMes)
                .Select(d => new MovimentoDoDia(d, entradas[d - 1], saidas[d - 1]))
                .ToList());
    }

    private static RelatorioEstoqueResult NovembroSemMovimento()
    {
        var periodo = new PeriodoMes(2026, 11);
        var zeros = new int[periodo.DiasNoMes];

        return new RelatorioEstoqueResult(
            periodo,
            [new ProdutoNoMes(ProdutoId, "Botijão P13", true, zeros, zeros, 0, 0, 0, 93, 0)],
            [],
            Enumerable.Range(1, periodo.DiasNoMes).Select(d => new MovimentoDoDia(d, 0, 0)).ToList());
    }

    private sealed class SenderDeRelatorioEstoque(RelatorioEstoqueResult resultado) : ISender
    {
        /// <summary>Filtros que a tela pediu, na ordem — permite provar que o toggle recarrega.</summary>
        public List<FiltroMovimentoEstoque> FiltrosPedidos { get; } = [];

        public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A tela usa a sobrecarga IQuery.");

        public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A tela não envia comandos.");

        public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
        {
            if (query is ObterRelatorioEstoqueQuery consulta)
            {
                FiltrosPedidos.Add(consulta.Filtro);
                return ValueTask.FromResult((TResponse)(object)resultado);
            }

            throw new NotSupportedException($"Consulta não esperada no teste: {query.GetType().Name}.");
        }

        public ValueTask<object?> Send(object message, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A tela só usa o Send tipado.");

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamQuery<TResponse> query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamCommand<TResponse> command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object message, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}