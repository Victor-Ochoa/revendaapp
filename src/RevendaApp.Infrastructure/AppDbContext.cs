using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Entities;
using RevendaApp.Infrastructure.Identity;
using RevendaApp.Infrastructure.Tenancy;

namespace RevendaApp.Infrastructure;

/// <summary>
/// Contexto EF Core da aplicação: dados de domínio (clientes, produtos, vendas, ...) e
/// autenticação (ASP.NET Core Identity, com usuários pertencendo a um papel — Admin ou Vendedor).
/// </summary>
/// <remarks>
/// <para>
/// Multi-empresa (multi-tenant): todas as tabelas de domínio têm <c>EmpresaId</c> e um filtro
/// global que isola os dados da empresa do usuário. O tenant vem do <see cref="ITenantContext"/>
/// opcional injetado no construtor; <c>null</c> = modo central (super admin, seed, testes e
/// reset), que enxerga todas as empresas.
/// </para>
/// <para>
/// O filtro usa a propriedade <see cref="_empresaId"/> (closure), avaliada <b>a cada query</b> — a
/// expressão é idêntica para todas as instâncias, então um único model cacheado serve a todos os
/// tenants.
/// </para>
/// <para>
/// <b>NÃO transforme <see cref="_empresaId"/> em campo <c>readonly</c> capturado no construtor.</b>
/// O contexto scoped pode ser construído ANTES do principal autenticado existir: durante o
/// prerender SSR o <c>AppDbContext</c> é resolvido de dentro do <c>SecurityStampValidator</c> da
/// autenticação por cookie, quando o <c>AuthenticationStateProvider</c> ainda não está no escopo.
/// Um campo readonly congelaria o tenant como <c>null</c> (modo central) e esse MESMO contexto
/// serviria a requisição inteira — vazando dados de todas as empresas para o usuário de uma delas.
/// Como propriedade, o valor é reavaliado quando a query executa (aí o principal já existe).
/// </para>
/// <para>
/// <b>Importante:</b> para o tenant ser o da requisição/circuito corrente, o <c>AppDbContext</c>
/// precisa ser registrado como <b>scoped</b> (não pooled). Ver o comentário em <c>Program.cs</c>
/// sobre por que usamos <c>AddDbContext</c> + <c>EnrichNpgsqlDbContext</c> em vez de
/// <c>AddNpgsqlDbContext</c> (que faz pooling e capturaria um tenant fixo da raiz).
/// </para>
/// </remarks>
public class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext? tenant = null)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options), IDataProtectionKeyContext
{
    /// <summary>
    /// Empresa do tenant atual, consultada <b>a cada avaliação</b> (propriedade, não campo
    /// capturado). <c>null</c> = modo central (vê tudo). O filtro global fecha sobre esta
    /// propriedade; o EF a parameteriza por execução de query, então um único model cacheado serve
    /// a todos os tenants e um contexto construído cedo (prerender/security stamp) passa a filtrar
    /// corretamente assim que o principal autenticado existe.
    /// </summary>
    /// <remarks>
    /// Deve permanecer uma <b>propriedade</b>: um campo <c>readonly</c> aqui congela o tenant do
    /// momento da construção e causa vazamento cross-tenant no prerender SSR. Ver o comentário da
    /// classe.
    /// </remarks>
    private Guid? _empresaId => tenant?.EmpresaId;

    public DbSet<Cliente> Clientes => Set<Cliente>();
    public DbSet<Produto> Produtos => Set<Produto>();
    public DbSet<Venda> Vendas => Set<Venda>();
    public DbSet<ItemVenda> ItensVenda => Set<ItemVenda>();
    public DbSet<Pagamento> Pagamentos => Set<Pagamento>();
    public DbSet<MovimentacaoEstoque> MovimentacoesEstoque => Set<MovimentacaoEstoque>();
    public DbSet<HistoricoEstoque> HistoricosEstoque => Set<HistoricoEstoque>();
    public DbSet<PrecoPersonalizadoCliente> PrecosPersonalizadosClientes => Set<PrecoPersonalizadoCliente>();
    public DbSet<EmprestimoVasilhame> EmprestimosVasilhames => Set<EmprestimoVasilhame>();
    public DbSet<RecorrenciaCompra> RecorrenciasCompra => Set<RecorrenciaCompra>();

    /// <summary>
    /// Trilha de auditoria das ações relevantes.
    /// </summary>
    /// <remarks>
    /// <b>Este DbSet é DELIBERADAMENTE sem filtro por empresa</b> (é o único DbSet de domínio
    /// nesse caso — ver <c>FiltrosGlobaisTests</c>, que trava a lista). A central precisa ler todas
    /// as empresas e cada empresa precisa ler as suas, então <c>EmpresaId</c> é anulável
    /// (<c>null</c> = ação da central) e atribuído explicitamente por quem grava.
    /// <para>
    /// <b>Consequência para quem lê:</b> uma consulta como
    /// <c>db.RegistrosAuditoria.OrderByDescending(r => r.DataHora)</c> compila igual a qualquer
    /// outra e devolve as linhas de <b>todas as empresas</b>. Todo leitor que atende uma empresa
    /// <b>deve</b> filtrar explicitamente (<c>r.EmpresaId == tenant.EmpresaIdRequerida(...)</c>);
    /// só a central pode consultar sem filtro.
    /// </para>
    /// </remarks>
    public DbSet<RegistroAuditoria> RegistrosAuditoria => Set<RegistroAuditoria>();

    /// <summary>
    /// Chaves de Data Protection (proteção de cookie de autenticação e de token antiforgery).
    /// </summary>
    /// <remarks>
    /// Tabela <b>global</b>: sem <c>EmpresaId</c>, sem filtro por empresa e sem carimbo do
    /// interceptor — as chaves valem para o processo inteiro, não para um tenant. Entra em
    /// <c>SemFiltroEsperado</c> de <c>FiltrosGlobaisTests</c>. Persistir no banco (e não no
    /// filesystem efêmero do container) é o que mantém os cookies válidos após um restart e
    /// permite mais de uma réplica com as mesmas chaves.
    /// </remarks>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Isolamento por empresa: cada tabela de domínio só enxerga linhas da empresa do tenant.
        // `_empresaId == null` = modo central → vê todas as empresas.
        // `Empresas` e as tabelas do Identity NÃO têm filtro.
        builder.Entity<Cliente>().HasQueryFilter(c => _empresaId == null || c.EmpresaId == _empresaId);
        builder.Entity<Produto>().HasQueryFilter(p => _empresaId == null || p.EmpresaId == _empresaId);
        builder.Entity<Venda>().HasQueryFilter(v => _empresaId == null || v.EmpresaId == _empresaId);
        builder.Entity<ItemVenda>().HasQueryFilter(i => _empresaId == null || i.EmpresaId == _empresaId);
        builder.Entity<Pagamento>().HasQueryFilter(p => _empresaId == null || p.EmpresaId == _empresaId);
        builder.Entity<MovimentacaoEstoque>().HasQueryFilter(m => _empresaId == null || m.EmpresaId == _empresaId);
        builder.Entity<HistoricoEstoque>().HasQueryFilter(h => _empresaId == null || h.EmpresaId == _empresaId);
        builder.Entity<PrecoPersonalizadoCliente>().HasQueryFilter(p => _empresaId == null || p.EmpresaId == _empresaId);
        builder.Entity<EmprestimoVasilhame>().HasQueryFilter(e => _empresaId == null || e.EmpresaId == _empresaId);
        builder.Entity<RecorrenciaCompra>().HasQueryFilter(r => _empresaId == null || r.EmpresaId == _empresaId);
    }
}