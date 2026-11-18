using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor;
using MudBlazor.Services;

using RevendaApp.Web.Common.Relatorios;
using RevendaApp.Web.Features.Relatorios.Shell;

namespace RevendaApp.UnitTests.Components;

/// <summary>
/// O shell é o seletor de mês/ano de todos os relatórios. Ele renderiza o <c>MudSelect</c> com 12
/// opções; a regressão que estes testes pinam é a captura da variável de laço: os
/// <c>RenderFragment</c> dos itens são avaliados depois do <c>for</c>, e sem uma cópia local do
/// contador todos os itens recebem o valor final (13), estourando
/// <c>new DateTime(2000, 13, 1)</c> em runtime.
/// </summary>
public class RelatorioShellTests
{
    private static BunitContext CriarContexto()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    [Fact]
    public async Task Shell_renderiza_os_doze_meses_sem_estourar_o_intervalo()
    {
        await using var ctx = CriarContexto();

        var cut = ctx.Render<RelatorioShell>(parameters => parameters
            .Add(p => p.Titulo, "Estoque")
            .Add(p => p.Mes, 11)
            .Add(p => p.Ano, 2026));

        // Controle positivo: o mês recebido aparece no campo e a renderização não estoura. O
        // MudSelect só materializa os itens ao abrir, então o que se prova aqui é que o laço dos
        // 12 meses rodou sem exceção e que o valor selecionado foi renderizado.
        // O MudSelect só materializa o markup dos itens ao abrir, mas os componentes existem na
        // árvore — é por aqui que se prova que os 12 meses foram montados, sem depender do popover.
        var itens = cut.FindComponents<MudSelectItem<int>>()
            .Select(item => item.Instance.Value)
            .ToList();

        Assert.Equal(12, itens.Count(item => item is >= 1 and <= 12));
        Assert.Contains(11, itens);
        Assert.Contains("novembro", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Shell_mostra_o_mes_e_o_ano_recebidos()
    {
        await using var ctx = CriarContexto();

        var cut = ctx.Render<RelatorioShell>(parameters => parameters
            .Add(p => p.Titulo, "Fiado")
            .Add(p => p.Descricao, "Quem deve")
            .Add(p => p.Mes, 11)
            .Add(p => p.Ano, 2026));

        Assert.Contains("Fiado", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Quem deve", cut.Markup, StringComparison.Ordinal);
        File.WriteAllText("/tmp/shell-markup.html", cut.Markup);
        Assert.Contains("novembro", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Shell_nao_quebra_quando_o_mes_ainda_nao_foi_informado()
    {
        await using var ctx = CriarContexto();

        // Mes/Ano são parâmetros: um pai que renderize antes de carregar o período passa 0. O
        // shell não pode transformar isso em ArgumentOutOfRangeException.
        var cut = ctx.Render<RelatorioShell>(parameters => parameters
            .Add(p => p.Titulo, "Vendas"));

        Assert.Contains("Vendas", cut.Markup, StringComparison.Ordinal);
    }
}