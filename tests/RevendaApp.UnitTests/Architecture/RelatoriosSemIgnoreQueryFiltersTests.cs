using System.IO;

namespace RevendaApp.UnitTests.Architecture;

/// <summary>
/// Os relatórios são leituras por empresa: um <c>IgnoreQueryFilters()</c> anularia o filtro global
/// e vazaria dados entre tenants. A tentação existe justamente ao montar agregações, então é
/// travado por teste.
/// </summary>
public class RelatoriosSemIgnoreQueryFiltersTests
{
    [Fact]
    public void Relatorios_nao_usam_IgnoreQueryFilters()
    {
        var raiz = new DirectoryInfo(AppContext.BaseDirectory);
        while (raiz is not null && !File.Exists(Path.Combine(raiz.FullName, "RevendaApp.slnx")))
        {
            raiz = raiz.Parent;
        }

        Assert.NotNull(raiz);

        var pastaRelatorios = Path.Combine(raiz!.FullName, "src", "RevendaApp.Web", "Features", "Relatorios");
        var arquivos = Directory.GetFiles(pastaRelatorios, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(arquivos);   // se a pasta sumir, o teste falha em vez de passar vazio

        var infratores = arquivos
            .Where(f => File.ReadAllText(f).Contains("IgnoreQueryFilters", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(infratores);
    }
}