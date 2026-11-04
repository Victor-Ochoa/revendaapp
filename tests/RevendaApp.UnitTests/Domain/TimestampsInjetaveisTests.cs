using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;

namespace RevendaApp.UnitTests.Domain;

/// <summary>
/// Os instantes de auditoria precisam ser injetáveis: sem isso não dá para testar "excluída em"
/// nem manter o histórico coerente com o <c>TimeProvider</c> usado pelo resto do handler.
/// </summary>
public class TimestampsInjetaveisTests
{
    [Fact]
    public void Excluir_usa_o_instante_informado()
    {
        var venda = new Venda(Guid.NewGuid(), FormaPagamento.Dinheiro);
        var instante = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        venda.Excluir(instante);

        Assert.Equal(instante, venda.ExcluidaEm);
    }

    [Fact]
    public void Excluir_sem_instante_usa_agora()
    {
        var venda = new Venda(Guid.NewGuid(), FormaPagamento.Dinheiro);
        var antes = DateTime.UtcNow.AddSeconds(-1);

        venda.Excluir();

        Assert.NotNull(venda.ExcluidaEm);
        Assert.True(venda.ExcluidaEm >= antes);
    }

    [Fact]
    public void Excluir_nao_sobrescreve_o_instante_da_primeira_chamada()
    {
        var venda = new Venda(Guid.NewGuid(), FormaPagamento.Dinheiro);
        var primeiro = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        venda.Excluir(primeiro);
        venda.Excluir(primeiro.AddDays(1));

        Assert.Equal(primeiro, venda.ExcluidaEm);
    }

    [Fact]
    public void RegistrarDevolucao_usa_o_instante_informado()
    {
        var emprestimo = new EmprestimoVasilhame(Guid.NewGuid(), Guid.NewGuid(), 2);
        var instante = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        emprestimo.RegistrarDevolucao(2, instante);

        Assert.Equal(instante, emprestimo.DevolvidoEm);
    }

    [Fact]
    public void RegistrarDevolucao_parcial_nao_define_o_instante()
    {
        var emprestimo = new EmprestimoVasilhame(Guid.NewGuid(), Guid.NewGuid(), 5);
        var instante = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        emprestimo.RegistrarDevolucao(2, instante);

        Assert.Null(emprestimo.DevolvidoEm);
    }

    [Fact]
    public void RegistrarDevolucao_sem_instante_usa_agora()
    {
        var emprestimo = new EmprestimoVasilhame(Guid.NewGuid(), Guid.NewGuid(), 2);
        var antes = DateTime.UtcNow.AddSeconds(-1);

        emprestimo.RegistrarDevolucao(2);

        Assert.NotNull(emprestimo.DevolvidoEm);
        Assert.True(emprestimo.DevolvidoEm >= antes);
    }
}