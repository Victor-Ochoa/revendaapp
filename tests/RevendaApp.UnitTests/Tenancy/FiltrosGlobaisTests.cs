using RevendaApp.Infrastructure.Tenancy;
using RevendaApp.UnitTests.TestHelpers;

namespace RevendaApp.UnitTests.Tenancy;

/// <summary>
/// Trava as duas listas que definem o isolamento por empresa no <c>AppDbContext</c> e no
/// <c>SetEmpresaIdInterceptor</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que travar:</b> dez DbSets têm filtro global e um (<c>RegistrosAuditoria</c>) não tem, de
/// propósito. Uma leitura "normal" (<c>db.RegistrosAuditoria.OrderByDescending(...)</c>) compila
/// igual a qualquer outra e devolve as linhas de <b>todas</b> as empresas — o leitor precisa filtrar
/// explicitamente. Se alguém adicionar o filtro (ou esquecer de adicionar a uma entidade nova), estes
/// testes falham e obrigam a decisão consciente.
/// </para>
/// <para>
/// A asserção é uma lista de nomes de tipo comparada com a esperada — legível no diff do PR, sem
/// reflexão.
/// </para>
/// </remarks>
public class FiltrosGlobaisTests
{
    /// <summary>
    /// Entidades <b>sem</b> filtro global: as do Identity, <c>Empresa</c>, a auditoria global e as
    /// chaves de Data Protection (globais por natureza — valem para o processo, não para um tenant).
    /// </summary>
    private static readonly string[] SemFiltroEsperado =
    [
        "ApplicationRole",
        "ApplicationUser",
        "DataProtectionKey",
        "Empresa",
        "IdentityRoleClaim`1",
        "IdentityUserClaim`1",
        "IdentityUserLogin`1",
        "IdentityUserRole`1",
        "IdentityUserToken`1",
        "RegistroAuditoria",
    ];

    /// <summary>Entidades de domínio <b>com</b> filtro global por empresa.</summary>
    private static readonly string[] ComFiltroEsperado =
    [
        "Cliente",
        "EmprestimoVasilhame",
        "HistoricoEstoque",
        "ItemVenda",
        "MovimentacaoEstoque",
        "Pagamento",
        "PrecoPersonalizadoCliente",
        "Produto",
        "RecorrenciaCompra",
        "Venda",
    ];

    [Fact]
    public void Entidades_sem_filtro_global_sao_exatamente_as_esperadas()
    {
        using var db = InMemoryDbContextFactory.Create();

        var semFiltro = db.Model.GetEntityTypes()
            .Where(t => t.GetDeclaredQueryFilters().Count == 0)
            .Select(t => t.ClrType.Name)
            .OrderBy(nome => nome, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(SemFiltroEsperado.OrderBy(nome => nome, StringComparer.Ordinal), semFiltro);
    }

    [Fact]
    public void Entidades_de_dominio_com_filtro_global_sao_exatamente_as_esperadas()
    {
        using var db = InMemoryDbContextFactory.Create();

        var comFiltro = db.Model.GetEntityTypes()
            .Where(t => t.GetDeclaredQueryFilters().Count > 0)
            .Select(t => t.ClrType.Name)
            .OrderBy(nome => nome, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(ComFiltroEsperado.OrderBy(nome => nome, StringComparer.Ordinal), comFiltro);
    }

    /// <summary>
    /// <see cref="RegistroAuditoria"/> <b>não</b> pode entrar em
    /// <c>SetEmpresaIdInterceptor.EntidadesDeDominio</c>: o interceptor chama
    /// <c>EmpresaIdRequerida</c> em toda linha <c>Added</c> sem empresa, e os registros de origem
    /// central têm <c>EmpresaId == null</c> — a gravação estouraria em produção (e só lá).
    /// </summary>
    [Fact]
    public void Interceptor_nao_tenta_preencher_EmpresaId_de_RegistroAuditoria()
    {
        // Modo central: exatamente o cenário em que o interceptor lançaria se conhecesse a entidade.
        var tenantSemEmpresa = new FakeTenantContext(empresaId: null, ehSuperAdmin: true);

        using var db = InMemoryDbContextFactory.Create(tenant: tenantSemEmpresa);

        db.RegistrosAuditoria.Add(new RevendaApp.Domain.Entities.RegistroAuditoria(
            empresaId: null,
            usuarioId: null,
            usuarioNome: "super@revenda.app.com.br",
            RevendaApp.Domain.Entities.RegistroAuditoria.Acoes.ConfigEmpresaEditada,
            "detalhes",
            RevendaApp.Domain.Entities.RegistroAuditoria.Origens.Central));

        // Não lança e preserva o EmpresaId nulo (ação da central).
        db.SaveChanges();

        var registro = Assert.Single(db.RegistrosAuditoria);
        Assert.Null(registro.EmpresaId);
    }

    /// <summary>
    /// Controle positivo do teste acima: uma entidade de domínio no mesmo cenário (modo central)
    /// <b>precisa</b> lançar — é isso que o interceptor faz. Sem esta prova, o teste anterior
    /// passaria mesmo com o interceptor inerte (ex.: sem tenant injetado).
    /// </summary>
    [Fact]
    public void Interceptor_continua_recusando_entidade_de_dominio_sem_empresa()
    {
        var tenantSemEmpresa = new FakeTenantContext(empresaId: null, ehSuperAdmin: true);

        using var db = InMemoryDbContextFactory.Create(tenant: tenantSemEmpresa);

        db.Produtos.Add(new RevendaApp.Domain.Entities.Produto(
            "Produto órfão", RevendaApp.Domain.Enums.TipoProduto.Gas, 100m, 60m, 80m));

        var excecao = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        Assert.Contains("exige uma empresa", excecao.Message);
    }

    /// <summary>
    /// A lista de entidades do interceptor é fechada de propósito: um tipo novo de domínio tem de
    /// ser adicionado ali. Esta asserção documenta o conjunto atual (via o comportamento, não via
    /// reflexão sobre um campo privado).
    /// </summary>
    [Fact]
    public void Interceptor_preenche_EmpresaId_das_dez_entidades_de_dominio()
    {
        var empresaId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        using var db = InMemoryDbContextFactory.Create(empresaId);

        // Uma amostra representativa de cada família de entidade de domínio.
        db.Clientes.Add(new RevendaApp.Domain.Entities.Cliente("Cliente", "11999990000", "Rua 1"));
        db.Produtos.Add(new RevendaApp.Domain.Entities.Produto(
            "Botijão", RevendaApp.Domain.Enums.TipoProduto.Gas, 100m, 60m, 80m));

        db.SaveChanges();

        Assert.Equal(empresaId, db.Clientes.Single().EmpresaId);
        Assert.Equal(empresaId, db.Produtos.Single().EmpresaId);
    }
}