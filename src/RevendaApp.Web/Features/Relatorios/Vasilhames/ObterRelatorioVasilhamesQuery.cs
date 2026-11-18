using Mediator;

using RevendaApp.Web.Common.Relatorios;

namespace RevendaApp.Web.Features.Relatorios.Vasilhames;

/// <summary>Vasilhames na rua por cliente e produto, e o movimento do mês.</summary>
public sealed record ObterRelatorioVasilhamesQuery(PeriodoMes Periodo) : IQuery<RelatorioVasilhamesResult>;

public sealed record RelatorioVasilhamesResult(
    PeriodoMes Periodo,
    IReadOnlyList<VasilhamePorProduto> PorProduto,
    IReadOnlyList<EmprestimoEmAbertoItem> EmAberto,
    IReadOnlyList<DevolucaoDoDia> DevolucoesPorDia,
    int TotalEmprestadoNoMes,
    int TotalDevolvidoNoMes);

public sealed record VasilhamePorProduto(
    Guid ProdutoId,
    string Nome,
    int EmprestadosNoMes,
    int DevolvidosNoMes,
    int EmAberto);

/// <summary>Um empréstimo em aberto agrupado por cliente + produto (o saldo é a soma).</summary>
public sealed record EmprestimoEmAbertoItem(
    Guid ClienteId,
    string ClienteNome,
    Guid ProdutoId,
    string ProdutoNome,
    int Saldo,
    DateTime DataMaisAntiga);

public sealed record DevolucaoDoDia(int Dia, int Quantidade);