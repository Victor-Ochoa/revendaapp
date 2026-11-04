using Microsoft.EntityFrameworkCore;

using RevendaApp.UnitTests.TestHelpers;

namespace RevendaApp.UnitTests.Infrastructure;

/// <summary>
/// Trava os índices compostos que sustentam as consultas do dashboard. Sem eles, o filtro por
/// empresa + período degrada para seq scan à medida que o histórico de vendas cresce.
/// </summary>
public class IndicesDashboardTests
{
    [Theory]
    [InlineData("Vendas", "EmpresaId", "DataHora")]
    [InlineData("Pagamentos", "EmpresaId", "Data")]
    [InlineData("Clientes", "EmpresaId", "SaldoDevedor")]
    public void Entidade_possui_indice_composto_por_empresa(string entidade, string primeira, string segunda)
    {
        using var db = InMemoryDbContextFactory.Create();

        var tipo = db.Model.GetEntityTypes().Single(e => e.GetTableName() == entidade);
        var possui = tipo.GetIndexes().Any(i =>
            i.Properties.Select(p => p.Name).SequenceEqual([primeira, segunda]));

        Assert.True(possui, $"{entidade} deveria ter índice ({primeira}, {segunda}).");
    }
}