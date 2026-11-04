using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.Infrastructure;
using RevendaApp.Infrastructure.Identity;
using RevendaApp.Infrastructure.Tenancy;
using RevendaApp.IntegrationTests.TestHelpers;
using RevendaApp.Web.Features.Auth.Login;
using RevendaApp.Web.Features.Central.Auditoria;
using RevendaApp.Web.Features.Central.CriarEmpresa;
using RevendaApp.Web.Features.Central.EditarEmpresa;
using RevendaApp.Web.Features.Central.InativarEmpresa;
using RevendaApp.Web.Features.Central.ListarEmpresas;
using RevendaApp.Web.Features.Central.ListarProdutosEmpresa;
using RevendaApp.Web.Features.Central.UsuariosEmpresa;
using RevendaApp.Web.Features.Usuarios.Cadastrar;

namespace RevendaApp.IntegrationTests.Tests;

/// <summary>
/// Testes de integração da <b>central do super admin</b> contra o Postgres real (via
/// <see cref="DatabaseFixture"/>): criar/editar/inativar empresas, criar/inativar usuários de uma
/// empresa, listar produtos (somente leitura) e o reforço de super admin no servidor.
/// </summary>
/// <remarks>
/// <para>
/// Os handlers da central rodam em <b>modo central</b> (<c>ITenantContext.EmpresaId == null</c> e
/// <c>EhSuperAdmin == true</c>). A preparação/inspeção cross-tenant usa
/// <see cref="DatabaseFixture.CreateDbContextCentral"/>; a criação de dados de uma empresa usa
/// <see cref="DatabaseFixture.CreateDbContext(Guid?)"/> com o id dela (o interceptor tagueia
/// <c>EmpresaId</c> e o filtro global fecha o tenant).
/// </para>
/// <para>
/// <b>Fidelidade do DI:</b> <see cref="CriarServicos"/> monta um escopo com o mesmo
/// <c>AddIdentity</c> de produção, de modo que o <see cref="UserManager{TUser}"/> e o
/// <see cref="AppDbContext"/> do handler sejam a <b>mesma instância scoped</b> — é isso que faz a
/// transação de <c>CriarEmpresa</c> cobrir empresa + usuário.
/// </para>
/// </remarks>
public class EmpresaGerenciamentoTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CriarEmpresa_cria_empresa_e_primeiro_admin()
    {
        await using var servicos = CriarServicos();
        var email = EmailUnico("admin-empresa-nova");

        var resultado = await servicos.CriarEmpresaHandler.Handle(
            ComandoCriarEmpresa("André Gás", "andre-gas", email, "Senha@123", "Administrador André"),
            CancellationToken.None);

        Assert.True(resultado.Sucesso, resultado.Erro);
        Assert.NotNull(resultado.EmpresaId);
        Assert.NotNull(resultado.AdminId);

        using var db = fixture.CreateDbContextCentral();
        var empresa = await db.Set<Empresa>().SingleAsync(e => e.Id == resultado.EmpresaId);
        Assert.Equal("andre-gas", empresa.Identificador);
        Assert.Equal("André Gás", empresa.Nome);
        Assert.True(empresa.Ativo);

        var admin = await db.Users.SingleAsync(u => u.Id == resultado.AdminId);
        Assert.Equal(empresa.Id, admin.EmpresaId);
        Assert.True(admin.Ativo);
        Assert.True(await servicos.UserManager.IsInRoleAsync(admin, ApplicationRole.Admin));
        Assert.Equal(email, admin.Email);
    }

    [Fact]
    public async Task CriarEmpresa_com_identificador_duplicado_falha_com_mensagem()
    {
        await using var servicos = CriarServicos();

        var primeira = await servicos.CriarEmpresaHandler.Handle(
            ComandoCriarEmpresa("André Gás", "andre-gas", EmailUnico("admin1"), "Senha@123", "Admin 1"),
            CancellationToken.None);
        Assert.True(primeira.Sucesso, primeira.Erro);

        var comandoDuplicado = ComandoCriarEmpresa(
            "Outra Gás", "andre-gas", EmailUnico("admin2"), "Senha@123", "Admin 2");

        // O validador (usado pelo pipeline do Mediator na UI) já barra com mensagem pt-BR...
        var validacao = await new CriarEmpresaCommandValidator(servicos.Db).ValidateAsync(comandoDuplicado);
        Assert.False(validacao.IsValid);
        Assert.Contains(validacao.Errors, e =>
            e.PropertyName == nameof(CriarEmpresaCommand.Identificador)
            && e.ErrorMessage.Contains("identificador", StringComparison.OrdinalIgnoreCase));

        // ...e o handler também devolve erro (defesa em profundidade contra corrida), sem criar
        // uma segunda empresa nem um usuário órfão.
        var segunda = await servicos.CriarEmpresaHandler.Handle(comandoDuplicado, CancellationToken.None);

        Assert.False(segunda.Sucesso);
        Assert.Null(segunda.EmpresaId);
        Assert.False(string.IsNullOrWhiteSpace(segunda.Erro));

        using var db = fixture.CreateDbContextCentral();
        Assert.Equal(1, await db.Set<Empresa>().CountAsync(e => e.Identificador == "andre-gas"));
        Assert.Equal(1, await db.Users.CountAsync(u => u.EmpresaId != null));
    }

    [Fact]
    public async Task CriarEmpresa_com_usuario_duplicado_nao_deixa_empresa_orfa()
    {
        await using var servicos = CriarServicos();
        var emailJaUsado = EmailUnico("repetido");

        var primeira = await servicos.CriarEmpresaHandler.Handle(
            ComandoCriarEmpresa("Primeira Gás", "primeira-gas", emailJaUsado, "Senha@123", "Admin Primeira"),
            CancellationToken.None);
        Assert.True(primeira.Sucesso, primeira.Erro);

        // A falha do Identity (e-mail já usado) acontece DEPOIS de a empresa ter sido salva: sem
        // transação, a empresa ficaria órfã (sem nenhum administrador).
        var segunda = await servicos.CriarEmpresaHandler.Handle(
            ComandoCriarEmpresa("Segunda Gás", "segunda-gas", emailJaUsado, "Senha@123", "Admin Segunda"),
            CancellationToken.None);

        Assert.False(segunda.Sucesso);
        Assert.False(string.IsNullOrWhiteSpace(segunda.Erro));

        using var db = fixture.CreateDbContextCentral();
        Assert.False(await db.Set<Empresa>().AnyAsync(e => e.Identificador == "segunda-gas"));

        // Controle positivo: a primeira empresa continua íntegra (o rollback não derrubou nada dela).
        var primeiraEmpresa = await db.Set<Empresa>().SingleAsync(e => e.Identificador == "primeira-gas");
        Assert.True(await db.Users.AnyAsync(u => u.EmpresaId == primeiraEmpresa.Id));
    }

    /// <summary>
    /// A atomicidade só é provada de verdade quando a falha ocorre <b>depois</b> do insert da
    /// empresa. O teste acima usa um e-mail repetido, mas o handler curto-circuita em
    /// <c>EmailJaUsadoAsync</c> <b>antes</b> da transação — ele passaria até com a transação
    /// removida. Aqui o <see cref="IUsuarioWriter"/> mente na checagem de e-mail e falha na criação
    /// do usuário, que é exatamente o ponto que a transação precisa cobrir.
    /// </summary>
    [Fact]
    public async Task CriarEmpresa_falha_do_identity_depois_do_insert_desfaz_a_empresa()
    {
        var emailQueFalha = EmailUnico("identity-falha");

        // Controle positivo dentro do mesmo escopo: um e-mail que não é o alvo usa o writer real.
        await using var servicos = CriarServicos(writer => new UsuarioWriterQueFalhaAposInsert(writer, emailQueFalha));

        var comFalha = await servicos.CriarEmpresaHandler.Handle(
            ComandoCriarEmpresa("Empresa Falha", "empresa-falha", emailQueFalha, "Senha@123", "Admin Falha"),
            CancellationToken.None);

        Assert.False(comFalha.Sucesso);
        Assert.Null(comFalha.EmpresaId);
        Assert.Contains("falha simulada", comFalha.Erro!, StringComparison.Ordinal);

        using (var db = fixture.CreateDbContextCentral())
        {
            // A empresa chegou a ser inserida (o writer só falhou depois) e o rollback a removeu.
            Assert.False(await db.Set<Empresa>().AnyAsync(e => e.Identificador == "empresa-falha"));
            Assert.False(await db.Users.AnyAsync(u => u.Email == emailQueFalha));
        }

        // Controle positivo: o mesmo escopo ainda cria empresa + admin normalmente.
        var ok = await servicos.CriarEmpresaHandler.Handle(
            ComandoCriarEmpresa("Empresa Ok", "empresa-ok", EmailUnico("ok"), "Senha@123", "Admin Ok"),
            CancellationToken.None);
        Assert.True(ok.Sucesso, ok.Erro);

        using var verificacao = fixture.CreateDbContextCentral();
        Assert.True(await verificacao.Users.AnyAsync(u => u.EmpresaId == ok.EmpresaId));
    }

    /// <summary>
    /// O <see cref="AppDbContext"/> é scoped ao circuito: se a <c>Empresa</c> de um rollback ficasse
    /// <c>Added</c> no change tracker, o super admin corrigiria o e-mail, clicaria Salvar de novo e
    /// duas empresas seriam inseridas (uma órfã). Aqui a primeira tentativa falha e a segunda, no
    /// mesmo escopo, tem de funcionar e produzir <b>uma</b> empresa.
    /// </summary>
    [Fact]
    public async Task CriarEmpresa_apos_falha_a_nova_tentativa_nao_duplica_a_empresa()
    {
        var emailQueFalha = EmailUnico("retry-falha");
        await using var servicos = CriarServicos(writer => new UsuarioWriterQueFalhaAposInsert(writer, emailQueFalha));

        var primeiraTentativa = await servicos.CriarEmpresaHandler.Handle(
            ComandoCriarEmpresa("Gás Retry", "gas-retry", emailQueFalha, "Senha@123", "Admin Retry"),
            CancellationToken.None);
        Assert.False(primeiraTentativa.Sucesso);

        // O operador corrige o e-mail e clica Salvar de novo, no mesmo escopo/circuito.
        var segundaTentativa = await servicos.CriarEmpresaHandler.Handle(
            ComandoCriarEmpresa("Gás Retry", "gas-retry", EmailUnico("retry-ok"), "Senha@123", "Admin Retry"),
            CancellationToken.None);

        Assert.True(segundaTentativa.Sucesso, segundaTentativa.Erro);

        using var db = fixture.CreateDbContextCentral();
        var empresa = Assert.Single(await db.Set<Empresa>().Where(e => e.Identificador == "gas-retry").ToListAsync());
        Assert.Equal(1, await db.Users.CountAsync(u => u.EmpresaId == empresa.Id));
    }

    [Fact]
    public async Task EditarEmpresa_altera_nome_cnpj_endereco_cor_logo_e_preserva_identificador()
    {
        await using var servicos = CriarServicos();

        var criada = await servicos.CriarEmpresaHandler.Handle(
            ComandoCriarEmpresa("André Gás", "andre-gas", EmailUnico("admin-editar"), "Senha@123", "Admin Editar"),
            CancellationToken.None);
        Assert.True(criada.Sucesso, criada.Erro);
        var empresaId = criada.EmpresaId!.Value;

        var logo = "data:image/png;base64,iVBORw0KGgo=";
        var editado = await servicos.EditarEmpresaHandler.Handle(
            new EditarEmpresaCommand(
                empresaId,
                "André Gás e Água",
                Cnpj: "12.345.678/0001-99",
                Endereco: "Rua Nova",
                Numero: "42",
                Cep: "01001-000",
                Bairro: "Centro",
                Cidade: "São Paulo",
                Estado: "SP",
                Telefones: ["11 98888-7777"],
                CorPrimaria: "#0A1F44",
                Logo: logo),
            CancellationToken.None);

        Assert.True(editado);

        using var db = fixture.CreateDbContextCentral();
        var empresa = await db.Set<Empresa>().SingleAsync(e => e.Id == empresaId);
        Assert.Equal("André Gás e Água", empresa.Nome);
        Assert.Equal("12.345.678/0001-99", empresa.Cnpj);
        Assert.Equal("Rua Nova", empresa.Endereco);
        Assert.Equal("42", empresa.Numero);
        Assert.Equal("01001-000", empresa.Cep);
        Assert.Equal("Centro", empresa.Bairro);
        Assert.Equal("São Paulo", empresa.Cidade);
        Assert.Equal("SP", empresa.Estado);
        Assert.Equal("11988887777", Assert.Single(empresa.Telefones)); // normalizado para dígitos
        Assert.Equal("#0A1F44", empresa.CorPrimaria);
        Assert.Equal(logo, empresa.Logo);

        // Identificador é imutável: o comando nem o carrega e o domínio não o altera.
        Assert.Equal("andre-gas", empresa.Identificador);
    }

    [Fact]
    public async Task InativarEmpresa_impede_login_do_admin()
    {
        await using var servicos = CriarServicos();
        var email = EmailUnico("admin-inativar");

        var criada = await servicos.CriarEmpresaHandler.Handle(
            ComandoCriarEmpresa("Gás Inativada", "gas-inativada", email, "Senha@123", "Admin Inativar"),
            CancellationToken.None);
        Assert.True(criada.Sucesso, criada.Erro);
        var empresaId = criada.EmpresaId!.Value;

        // Controle positivo: antes de inativar, o login do admin passa pelo gate de empresa.
        var signIn = new FakePasswordSignIn();
        var login = new LoginCommandHandler(signIn, servicos.UserManager, servicos.Db);
        var antes = await login.Handle(new LoginCommand(email, "Senha@123", false), CancellationToken.None);
        Assert.True(antes.Succeeded, antes.ErrorMessage);
        Assert.Equal(1, signIn.Chamadas);

        var inativada = await servicos.InativarEmpresaHandler.Handle(
            new InativarEmpresaCommand(empresaId, Ativar: false), CancellationToken.None);
        Assert.True(inativada);

        var depois = await login.Handle(new LoginCommand(email, "Senha@123", false), CancellationToken.None);

        Assert.False(depois.Succeeded);
        Assert.Contains("Empresa desativada", depois.ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal(1, signIn.Chamadas); // o gate roda ANTES do sign-in

        // Reativar devolve o acesso (não inativamos usuários em cascata).
        Assert.True(await servicos.InativarEmpresaHandler.Handle(
            new InativarEmpresaCommand(empresaId, Ativar: true), CancellationToken.None));

        var reativada = await login.Handle(new LoginCommand(email, "Senha@123", false), CancellationToken.None);
        Assert.True(reativada.Succeeded, reativada.ErrorMessage);
    }

    [Fact]
    public async Task ListarProdutosEmpresa_retorna_somente_produtos_da_empresa_alvo()
    {
        await using var servicos = CriarServicos();

        var empresaA = await CriarEmpresaComAdminAsync(servicos, "Empresa A", "empresa-a-produtos");
        var empresaB = await CriarEmpresaComAdminAsync(servicos, "Empresa B", "empresa-b-produtos");

        using (var dbA = fixture.CreateDbContext(empresaA))
        {
            dbA.Produtos.Add(new Produto("Botijão 13kg A", TipoProduto.Gas, 100m, 60m, 80m));
            await dbA.SaveChangesAsync();
        }

        using (var dbB = fixture.CreateDbContext(empresaB))
        {
            dbB.Produtos.Add(new Produto("Água 20L B", TipoProduto.Agua, 10m, 5m, 8m));
            await dbB.SaveChangesAsync();
        }

        var produtosDeA = await servicos.ListarProdutosEmpresaHandler.Handle(
            new ListarProdutosEmpresaQuery(empresaA), CancellationToken.None);

        var produtoA = Assert.Single(produtosDeA);
        Assert.Equal("Botijão 13kg A", produtoA.Nome);

        // Controle positivo cruzado: a query da empresa B não devolve o produto de A.
        var produtosDeB = await servicos.ListarProdutosEmpresaHandler.Handle(
            new ListarProdutosEmpresaQuery(empresaB), CancellationToken.None);
        var produtoB = Assert.Single(produtosDeB);
        Assert.Equal("Água 20L B", produtoB.Nome);
    }

    [Fact]
    public async Task CriarUsuarioEmpresa_cria_usuario_na_empresa_alvo()
    {
        await using var servicos = CriarServicos();

        var empresaA = await CriarEmpresaComAdminAsync(servicos, "Empresa A", "empresa-a-usuarios");
        var empresaB = await CriarEmpresaComAdminAsync(servicos, "Empresa B", "empresa-b-usuarios");
        var email = EmailUnico("vendedor-a");

        var criado = await servicos.CriarUsuarioEmpresaHandler.Handle(
            new CriarUsuarioEmpresaCommand(empresaA, "Vendedor A", email, ApplicationRole.Vendedor, "Senha@123"),
            CancellationToken.None);

        Assert.NotNull(criado.UsuarioId);
        Assert.Null(criado.Erro);

        var usuariosDeA = await servicos.ListarUsuariosEmpresaHandler.Handle(
            new ListarUsuariosEmpresaQuery(empresaA), CancellationToken.None);
        var novo = Assert.Single(usuariosDeA, u => u.Email == email);
        Assert.Equal("Vendedor A", novo.Nome);
        Assert.Equal(ApplicationRole.Vendedor, novo.Papel);
        Assert.True(novo.Ativo);

        // Não aparece na listagem da empresa B.
        var usuariosDeB = await servicos.ListarUsuariosEmpresaHandler.Handle(
            new ListarUsuariosEmpresaQuery(empresaB), CancellationToken.None);
        Assert.DoesNotContain(usuariosDeB, u => u.Email == email);
    }

    [Fact]
    public async Task ListarEmpresas_filtra_por_busca_e_conta_usuarios_por_empresa()
    {
        await using var servicos = CriarServicos();

        var empresaA = await CriarEmpresaComAdminAsync(servicos, "Alfa Gás", "alfa-gas");
        var empresaB = await CriarEmpresaComAdminAsync(servicos, "Beta Água", "beta-agua");

        // Um segundo usuário só em Alfa: a contagem tem de ser por empresa (sem N+1 e sem somar).
        await servicos.CriarUsuarioEmpresaHandler.Handle(
            new CriarUsuarioEmpresaCommand(empresaA, "Segundo da Alfa", EmailUnico("segundo-alfa"), ApplicationRole.Vendedor, "Senha@123"),
            CancellationToken.None);

        var todas = await servicos.ListarEmpresasHandler.Handle(
            new ListarEmpresasQuery(Busca: null, Ativo: null), CancellationToken.None);

        var alfa = Assert.Single(todas, e => e.Id == empresaA);
        var beta = Assert.Single(todas, e => e.Id == empresaB);
        Assert.Equal(2, alfa.TotalUsuarios);
        Assert.Equal(1, beta.TotalUsuarios);
        Assert.True(alfa.Ativo);
        Assert.False(alfa.TemLogo);
        Assert.Equal("SP", alfa.Estado);

        // Busca por nome: só Alfa.
        var filtradas = await servicos.ListarEmpresasHandler.Handle(
            new ListarEmpresasQuery("Alfa", Ativo: null), CancellationToken.None);
        Assert.Equal(empresaA, Assert.Single(filtradas).Id);
    }

    /// <summary>
    /// <b>Todos</b> os handlers da central (comandos e queries) recusam um principal que não é super
    /// admin. A política da rota é só a primeira barreira; o <c>EhSuperAdmin</c> no handler é o limite
    /// real de segurança. A lista é fechada de propósito: um handler novo da central tem de entrar aqui.
    /// </summary>
    public static TheoryData<string> HandlersDaCentral => new(
        "ListarEmpresas",
        "ObterEmpresa",
        "CriarEmpresa",
        "EditarEmpresa",
        "InativarEmpresa",
        "ListarProdutosEmpresa",
        "ListarUsuariosEmpresa",
        "CriarUsuarioEmpresa",
        "InativarUsuarioEmpresa",
        "ListarAuditoria");

    [Theory]
    [MemberData(nameof(HandlersDaCentral))]
    public async Task Handlers_da_central_recusam_usuario_que_nao_e_super_admin(string handler)
    {
        await using var servicos = CriarServicos();
        var empresaId = await CriarEmpresaComAdminAsync(servicos, "Empresa Comum", "empresa-comum-guarda");

        // Tenant de um administrador de empresa (não é super admin da central).
        var tenantDeEmpresa = new FakeTenantContext(empresaId);
        var db = servicos.Db;

        Task Executar() => handler switch
        {
            "ListarEmpresas" => new ListarEmpresasQueryHandler(db, tenantDeEmpresa)
                .Handle(new ListarEmpresasQuery(null, null), CancellationToken.None).AsTask(),
            "ObterEmpresa" => new ObterEmpresaQueryHandler(db, tenantDeEmpresa)
                .Handle(new ObterEmpresaQuery(empresaId), CancellationToken.None).AsTask(),
            "CriarEmpresa" => new CriarEmpresaCommandHandler(db, tenantDeEmpresa, servicos.UsuarioWriter)
                .Handle(ComandoCriarEmpresa("Inválida", "invalida", EmailUnico("x"), "Senha@123", "X"),
                    CancellationToken.None).AsTask(),
            "EditarEmpresa" => new EditarEmpresaCommandHandler(db, tenantDeEmpresa)
                .Handle(new EditarEmpresaCommand(empresaId, "Nome Alterado"), CancellationToken.None).AsTask(),
            "InativarEmpresa" => new InativarEmpresaCommandHandler(db, tenantDeEmpresa)
                .Handle(new InativarEmpresaCommand(empresaId, Ativar: false), CancellationToken.None).AsTask(),
            "ListarProdutosEmpresa" => new ListarProdutosEmpresaQueryHandler(db, tenantDeEmpresa)
                .Handle(new ListarProdutosEmpresaQuery(empresaId), CancellationToken.None).AsTask(),
            "ListarUsuariosEmpresa" => new ListarUsuariosEmpresaQueryHandler(db, tenantDeEmpresa)
                .Handle(new ListarUsuariosEmpresaQuery(empresaId), CancellationToken.None).AsTask(),
            "CriarUsuarioEmpresa" => new CriarUsuarioEmpresaCommandHandler(db, tenantDeEmpresa, servicos.UsuarioWriter)
                .Handle(new CriarUsuarioEmpresaCommand(
                    empresaId, "Intruso", EmailUnico("intruso"), ApplicationRole.Vendedor, "Senha@123"),
                    CancellationToken.None).AsTask(),
            "InativarUsuarioEmpresa" => new InativarUsuarioEmpresaCommandHandler(db, tenantDeEmpresa, servicos.UserManager)
                .Handle(new InativarUsuarioEmpresaCommand(empresaId, Guid.NewGuid(), Ativo: false),
                    CancellationToken.None).AsTask(),
            "ListarAuditoria" => new ListarAuditoriaQueryHandler(db, tenantDeEmpresa)
                .Handle(new ListarAuditoriaQuery(), CancellationToken.None).AsTask(),
            _ => throw new ArgumentOutOfRangeException(nameof(handler), handler, "Handler desconhecido no teste de guarda."),
        };

        var excecao = await Assert.ThrowsAsync<InvalidOperationException>(Executar);
        Assert.Contains("super", excecao.Message, StringComparison.OrdinalIgnoreCase);

        // Nada foi criado nem alterado pelos comandos negados.
        using var verificacao = fixture.CreateDbContextCentral();
        Assert.False(await verificacao.Set<Empresa>().AnyAsync(e => e.Identificador == "invalida"));
        Assert.True(await verificacao.Set<Empresa>().Where(e => e.Id == empresaId).Select(e => e.Ativo).SingleAsync());
        Assert.DoesNotContain(
            await verificacao.Users.Where(u => u.EmpresaId == empresaId).ToListAsync(),
            u => u.Email != null && u.Email.StartsWith("intruso-", StringComparison.Ordinal));
    }

    /// <summary>
    /// O papel é validado <b>no handler</b>, não só no validador: chamar o handler direto com
    /// <c>SuperAdmin</c> criaria um usuário com <c>EmpresaId</c> <b>e</b> a role de super admin
    /// (passaria em <c>ApenasSuperAdmin</c> e em todos os guardas <c>tenant.EhSuperAdmin</c>).
    /// </summary>
    [Fact]
    public async Task CriarUsuarioEmpresa_recusa_papel_super_admin_no_handler()
    {
        await using var servicos = CriarServicos();
        var empresaId = await CriarEmpresaComAdminAsync(servicos, "Empresa Papel", "empresa-papel");
        var email = EmailUnico("escalada");

        var excecao = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await servicos.CriarUsuarioEmpresaHandler.Handle(
                new CriarUsuarioEmpresaCommand(empresaId, "Escalada", email, ApplicationRole.SuperAdmin, "Senha@123"),
                CancellationToken.None));

        Assert.Contains(ApplicationRole.SuperAdmin, excecao.Message, StringComparison.Ordinal);

        using var db = fixture.CreateDbContextCentral();
        Assert.False(await db.Users.AnyAsync(u => u.Email == email));
    }

    /// <summary>
    /// Inativar um usuário tem de <b>revogar a sessão viva</b>: sem renovar o
    /// <c>SecurityStamp</c>, o cookie já emitido continua válido até o
    /// <c>SecurityStampValidator</c> rodar (30 min). Reativar <b>não</b> pode mexer no stamp.
    /// </summary>
    [Fact]
    public async Task InativarUsuarioEmpresa_renova_security_stamp_ao_inativar_e_nao_ao_reativar()
    {
        await using var servicos = CriarServicos();
        var empresaId = await CriarEmpresaComAdminAsync(servicos, "Empresa Stamp", "empresa-stamp");
        var email = EmailUnico("stamp");

        var criado = await servicos.CriarUsuarioEmpresaHandler.Handle(
            new CriarUsuarioEmpresaCommand(empresaId, "Vendedor Stamp", email, ApplicationRole.Vendedor, "Senha@123"),
            CancellationToken.None);
        Assert.NotNull(criado.UsuarioId);
        var usuarioId = criado.UsuarioId!.Value;

        var stampOriginal = await StampAsync(usuarioId);
        Assert.False(string.IsNullOrEmpty(stampOriginal));

        var inativado = await servicos.InativarUsuarioEmpresaHandler.Handle(
            new InativarUsuarioEmpresaCommand(empresaId, usuarioId, Ativo: false), CancellationToken.None);
        Assert.True(inativado.Sucesso, inativado.Erro);

        var stampAposInativar = await StampAsync(usuarioId);
        Assert.NotEqual(stampOriginal, stampAposInativar);

        var reativado = await servicos.InativarUsuarioEmpresaHandler.Handle(
            new InativarUsuarioEmpresaCommand(empresaId, usuarioId, Ativo: true), CancellationToken.None);
        Assert.True(reativado.Sucesso, reativado.Erro);

        // Reativar não renova: a sessão derrubada na inativação continua exigindo novo login.
        Assert.Equal(stampAposInativar, await StampAsync(usuarioId));
    }

    /// <summary>
    /// Inativar a <b>empresa</b> não é cascata e não renova stamp de ninguém (o bloqueio de login
    /// já é do <c>LoginCommandHandler</c>); reativar devolve o acesso a todos sem uma segunda
    /// operação.
    /// </summary>
    [Fact]
    public async Task InativarEmpresa_nao_renova_security_stamp_dos_usuarios()
    {
        await using var servicos = CriarServicos();
        var empresaId = await CriarEmpresaComAdminAsync(servicos, "Empresa Stamp 2", "empresa-stamp-2");
        var usuarios = await servicos.ListarUsuariosEmpresaHandler.Handle(
            new ListarUsuariosEmpresaQuery(empresaId), CancellationToken.None);
        var admin = Assert.Single(usuarios);

        var stampAntes = await StampAsync(admin.Id);

        Assert.True(await servicos.InativarEmpresaHandler.Handle(
            new InativarEmpresaCommand(empresaId, Ativar: false), CancellationToken.None));

        Assert.Equal(stampAntes, await StampAsync(admin.Id));
    }

    [Fact]
    public async Task InativarUsuarioEmpresa_recusa_inativar_ultimo_admin_ativo()
    {
        await using var servicos = CriarServicos();

        var empresaId = await CriarEmpresaComAdminAsync(servicos, "Empresa Um Admin", "empresa-um-admin");
        var usuarios = await servicos.ListarUsuariosEmpresaHandler.Handle(
            new ListarUsuariosEmpresaQuery(empresaId), CancellationToken.None);
        var admin = Assert.Single(usuarios, u => u.Papel == ApplicationRole.Admin);

        var recusado = await servicos.InativarUsuarioEmpresaHandler.Handle(
            new InativarUsuarioEmpresaCommand(empresaId, admin.Id, Ativo: false), CancellationToken.None);

        Assert.False(recusado.Sucesso);
        Assert.Contains("administrador", recusado.Erro!, StringComparison.OrdinalIgnoreCase);

        // Controle positivo: com um segundo admin ativo, inativar o primeiro é permitido.
        var segundo = await servicos.CriarUsuarioEmpresaHandler.Handle(
            new CriarUsuarioEmpresaCommand(empresaId, "Segundo Admin", EmailUnico("segundo-admin"), ApplicationRole.Admin, "Senha@123"),
            CancellationToken.None);
        Assert.NotNull(segundo.UsuarioId);

        var permitido = await servicos.InativarUsuarioEmpresaHandler.Handle(
            new InativarUsuarioEmpresaCommand(empresaId, admin.Id, Ativo: false), CancellationToken.None);
        Assert.True(permitido.Sucesso, permitido.Erro);

        using var db = fixture.CreateDbContextCentral();
        Assert.False(await db.Users.Where(u => u.Id == admin.Id).Select(u => u.Ativo).SingleAsync());
    }

    [Fact]
    public async Task InativarUsuarioEmpresa_recusa_usuario_de_outra_empresa()
    {
        await using var servicos = CriarServicos();

        var empresaA = await CriarEmpresaComAdminAsync(servicos, "Empresa A", "empresa-a-guarda");
        var empresaB = await CriarEmpresaComAdminAsync(servicos, "Empresa B", "empresa-b-guarda");

        var usuariosDeB = await servicos.ListarUsuariosEmpresaHandler.Handle(
            new ListarUsuariosEmpresaQuery(empresaB), CancellationToken.None);
        var adminDeB = Assert.Single(usuariosDeB, u => u.Papel == ApplicationRole.Admin);

        // Usuário da empresa B passado com o id da empresa A: tem de ser recusado (nunca aceite
        // um id de usuário de outra empresa).
        var resultado = await servicos.InativarUsuarioEmpresaHandler.Handle(
            new InativarUsuarioEmpresaCommand(empresaA, adminDeB.Id, Ativo: false), CancellationToken.None);

        Assert.False(resultado.Sucesso);
        Assert.Contains("não encontrado", resultado.Erro!, StringComparison.OrdinalIgnoreCase);

        using var db = fixture.CreateDbContextCentral();
        Assert.True(await db.Users.Where(u => u.Id == adminDeB.Id).Select(u => u.Ativo).SingleAsync());
    }

    /// <summary>
    /// A guarda do último administrador ativo não pode ser check-then-act: com dois admins ativos,
    /// duas inativações simultâneas (uma por admin) não podem passar as duas — a empresa ficaria
    /// sem administrador. A contagem e a gravação rodam na mesma transação em
    /// <c>IsolationLevel.Serializable</c>, então uma das transações aborta (40001), é reexecutada
    /// pela execution strategy e então enxerga o estado novo, recusando.
    /// </summary>
    [Fact]
    public async Task InativarUsuarioEmpresa_duas_inativacoes_simultaneas_nao_deixam_empresa_sem_admin()
    {
        Guid empresaId;
        Guid adminA;
        Guid adminB;

        await using (var preparo = CriarServicos())
        {
            empresaId = await CriarEmpresaComAdminAsync(preparo, "Empresa Corrida", "empresa-corrida");
            var usuarios = await preparo.ListarUsuariosEmpresaHandler.Handle(
                new ListarUsuariosEmpresaQuery(empresaId), CancellationToken.None);
            adminA = Assert.Single(usuarios).Id;

            var segundo = await preparo.CriarUsuarioEmpresaHandler.Handle(
                new CriarUsuarioEmpresaCommand(empresaId, "Admin B", EmailUnico("admin-b"), ApplicationRole.Admin, "Senha@123"),
                CancellationToken.None);
            Assert.NotNull(segundo.UsuarioId);
            adminB = segundo.UsuarioId!.Value;
        }

        // Cada tentativa usa o seu próprio escopo (DbContext/conexão): a corrida é no banco, não
        // num objeto compartilhado.
        await using var servicoA = CriarServicos();
        await using var servicoB = CriarServicos();

        var tarefaA = Task.Run(() => servicoA.InativarUsuarioEmpresaHandler
            .Handle(new InativarUsuarioEmpresaCommand(empresaId, adminA, Ativo: false), CancellationToken.None).AsTask());
        var tarefaB = Task.Run(() => servicoB.InativarUsuarioEmpresaHandler
            .Handle(new InativarUsuarioEmpresaCommand(empresaId, adminB, Ativo: false), CancellationToken.None).AsTask());

        var resultados = await Task.WhenAll(tarefaA, tarefaB);

        Assert.Equal(1, resultados.Count(r => r.Sucesso));
        var recusado = Assert.Single(resultados, r => !r.Sucesso);
        Assert.Contains("administrador", recusado.Erro!, StringComparison.OrdinalIgnoreCase);

        // A empresa continua com exatamente um admin ativo.
        using var db = fixture.CreateDbContextCentral();
        var adminsAtivos = await (from u in db.Users
                                  join ur in db.Set<IdentityUserRole<Guid>>() on u.Id equals ur.UserId
                                  join r in db.Set<ApplicationRole>() on ur.RoleId equals r.Id
                                  where u.EmpresaId == empresaId && u.Ativo && r.Name == ApplicationRole.Admin
                                  select u.Id)
            .ToListAsync();
        Assert.Single(adminsAtivos);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static string EmailUnico(string prefixo) => $"{prefixo}-{Guid.NewGuid():N}@revenda.app.com.br";

    /// <summary>
    /// Lê o <c>SecurityStamp</c> direto do banco (contexto central, sem tracking): é o valor que o
    /// <c>SecurityStampValidator</c> compara com o claim do cookie para invalidar sessões.
    /// </summary>
    private async Task<string?> StampAsync(Guid usuarioId)
    {
        using var db = fixture.CreateDbContextCentral();
        return await db.Users.AsNoTracking()
            .Where(u => u.Id == usuarioId)
            .Select(u => u.SecurityStamp)
            .SingleAsync();
    }

    private static CriarEmpresaCommand ComandoCriarEmpresa(
        string nome, string identificador, string email, string senha, string nomeAdmin) =>
        new(nome, identificador, email, senha, nomeAdmin,
            Cnpj: null,
            Endereco: "Rua de Teste",
            Numero: "100",
            Cep: "01001000",
            Bairro: "Centro",
            Cidade: "São Paulo",
            Estado: "SP",
            Telefones: ["11999990000"],
            CorPrimaria: null,
            Logo: null);

    /// <summary>
    /// Cria uma empresa ativa com um admin ativo (identificador único por chamada) e devolve o Id.
    /// </summary>
    private static async Task<Guid> CriarEmpresaComAdminAsync(ServicosCentral servicos, string nome, string identificador)
    {
        // O Postgres do AppHost tem volume persistente: sufixo aleatório no slug (índice único)
        // para nunca colidir com uma execução anterior.
        var slug = $"{identificador}-{Guid.NewGuid():N}"[..Math.Min(identificador.Length + 9, 100)];

        var resultado = await servicos.CriarEmpresaHandler.Handle(
            ComandoCriarEmpresa(nome, slug, EmailUnico("admin"), "Senha@123", $"Admin {nome}"),
            CancellationToken.None);

        Assert.True(resultado.Sucesso, resultado.Erro);
        return resultado.EmpresaId!.Value;
    }

    /// <summary>
    /// Escopo de serviços central fiel à produção: <c>UserManager</c> e <c>AppDbContext</c> são a
    /// <b>mesma instância scoped</b>, então a transação de <c>CriarEmpresa</c> cobre empresa e
    /// usuário (como no app web).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Espelha o <c>Program.cs</c> de produção</b>, inclusive o
    /// <c>EnrichNpgsqlDbContext&lt;AppDbContext&gt;()</c>. Isso é essencial: o enrich liga
    /// <c>EnableRetryOnFailure()</c> e, com uma <b>execution strategy de retry</b>, qualquer
    /// <c>SaveChanges</c> dentro de <c>Database.BeginTransactionAsync()</c> lança
    /// <c>"The configured execution strategy 'NpgsqlRetryingExecutionStrategy' does not support
    /// user-initiated transactions"</c>. Um harness sem o enrich esconde esse erro — foi
    /// exatamente o que deixou a suíte verde na primeira versão do <c>CriarEmpresa</c>.
    /// </para>
    /// <para>
    /// O <c>EnrichNpgsqlDbContext</c> exige um <c>IHostApplicationBuilder</c> e lê a connection
    /// string de <c>ConnectionStrings:revendaapp</c> (mesmo nome do recurso no AppHost). O builder
    /// abaixo monta um host mínimo só para obter as opções enriquecidas (retry/telemetria/health)
    /// e as aplica ao <c>AddDbContext</c> scoped — o mesmo padrão do <c>Program.cs</c>.
    /// </para>
    /// </remarks>
    /// <param name="decorarWriter">
    /// Opcional: decora o <see cref="IUsuarioWriter"/> real (usado para forçar uma falha do Identity
    /// <b>depois</b> de a empresa já ter sido inserida).
    /// </param>
    private ServicosCentral CriarServicos(Func<IUsuarioWriter, IUsuarioWriter>? decorarWriter = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:revendaapp"] = fixture.ConnectionString;
        builder.AddServiceDefaults();

        // Mesmo encadeamento de produção: AddDbContext scoped (o tenant precisa ser resolvido por
        // escopo, não capturado no pool) + interceptor + enrich (retry/telemetria/health).
        builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            options.UseNpgsql(fixture.ConnectionString);
            options.AddInterceptors(serviceProvider.GetRequiredService<SetEmpresaIdInterceptor>());
        });
        builder.EnrichNpgsqlDbContext<AppDbContext>();

        builder.Services.AddScoped<SetEmpresaIdInterceptor>(_ =>
            new SetEmpresaIdInterceptor(new FakeTenantContext(empresaId: null, ehSuperAdmin: true)));

        builder.Services
            .AddIdentity<ApplicationUser, ApplicationRole>(options =>
            {
                options.SignIn.RequireConfirmedAccount = false;
                options.User.RequireUniqueEmail = true;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        builder.Services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));

        var host = builder.Build();
        return new ServicosCentral(host, decorarWriter);
    }

    /// <summary>
    /// Decora o <see cref="IUsuarioWriter"/> real: para o e-mail alvo, <c>EmailJaUsadoAsync</c>
    /// devolve <c>false</c> (passa pela pré-checagem do handler) e <c>CriarComRoleAsync</c> devolve
    /// erro <b>sem</b> criar nada. É a única forma de exercitar a falha do Identity depois do
    /// insert da empresa sem depender de uma regra de senha/e-mail do Identity.
    /// </summary>
    private sealed class UsuarioWriterQueFalhaAposInsert(IUsuarioWriter interno, string emailQueFalha) : IUsuarioWriter
    {
        public Task<bool> EmailJaUsadoAsync(string email, CancellationToken cancellationToken = default) =>
            string.Equals(email, emailQueFalha, StringComparison.OrdinalIgnoreCase)
                ? Task.FromResult(false)
                : interno.EmailJaUsadoAsync(email, cancellationToken);

        public Task<(Guid? UsuarioId, string? Erro)> CriarComRoleAsync(
            string nome, string email, string papel, string senha, Guid? empresaId,
            CancellationToken cancellationToken = default) =>
            string.Equals(email, emailQueFalha, StringComparison.OrdinalIgnoreCase)
                ? Task.FromResult<(Guid?, string?)>((null, "falha simulada do Identity"))
                : interno.CriarComRoleAsync(nome, email, papel, senha, empresaId, cancellationToken);
    }

    /// <summary>Fake de <see cref="IPasswordSignIn"/> que sempre autentica e conta as chamadas.</summary>
    private sealed class FakePasswordSignIn : IPasswordSignIn
    {
        public int Chamadas { get; private set; }

        public Task<SignInOutcome> PasswordSignInAsync(string email, string password, bool rememberMe)
        {
            Chamadas++;
            return Task.FromResult(SignInOutcome.Succeeded);
        }
    }

    /// <summary>Escopo de serviços em <b>modo central</b> (super admin), com os handlers da central.</summary>
    private sealed class ServicosCentral : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly IServiceScope _scope;

        public ServicosCentral(IHost host, Func<IUsuarioWriter, IUsuarioWriter>? decorarWriter = null)
        {
            _host = host;
            _scope = host.Services.CreateScope();

            Db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
            UserManager = _scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roleManager = _scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var writerReal = new IdentityUsuarioWriter(UserManager, roleManager);
            UsuarioWriter = decorarWriter is null ? writerReal : decorarWriter(writerReal);

            var tenant = new FakeTenantContext(empresaId: null, ehSuperAdmin: true);
            CriarEmpresaHandler = new CriarEmpresaCommandHandler(Db, tenant, UsuarioWriter);
            EditarEmpresaHandler = new EditarEmpresaCommandHandler(Db, tenant);
            InativarEmpresaHandler = new InativarEmpresaCommandHandler(Db, tenant);
            ListarEmpresasHandler = new ListarEmpresasQueryHandler(Db, tenant);
            ListarProdutosEmpresaHandler = new ListarProdutosEmpresaQueryHandler(Db, tenant);
            ListarUsuariosEmpresaHandler = new ListarUsuariosEmpresaQueryHandler(Db, tenant);
            CriarUsuarioEmpresaHandler = new CriarUsuarioEmpresaCommandHandler(Db, tenant, UsuarioWriter);
            InativarUsuarioEmpresaHandler = new InativarUsuarioEmpresaCommandHandler(Db, tenant, UserManager);
        }

        public AppDbContext Db { get; }

        public UserManager<ApplicationUser> UserManager { get; }

        public IUsuarioWriter UsuarioWriter { get; }

        public CriarEmpresaCommandHandler CriarEmpresaHandler { get; }

        public EditarEmpresaCommandHandler EditarEmpresaHandler { get; }

        public InativarEmpresaCommandHandler InativarEmpresaHandler { get; }

        public ListarEmpresasQueryHandler ListarEmpresasHandler { get; }

        public ListarProdutosEmpresaQueryHandler ListarProdutosEmpresaHandler { get; }

        public ListarUsuariosEmpresaQueryHandler ListarUsuariosEmpresaHandler { get; }

        public CriarUsuarioEmpresaCommandHandler CriarUsuarioEmpresaHandler { get; }

        public InativarUsuarioEmpresaCommandHandler InativarUsuarioEmpresaHandler { get; }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            _scope.Dispose();
            _host.Dispose();
        }
    }
}
/// <summary>
/// Testes HTTP do "Entrar como" a partir da central: a recusa de negócio (empresa inativa / sem
/// administrador ativo) precisa ser <b>visível</b> na tela, e não um 403 numa página de erro.
/// </summary>
/// <remarks>
/// Usa <see cref="WebAppFixture"/> porque o caminho testado é o endpoint POST
/// <c>/central/entrar-como</c> (troca de cookie + redirect), não um handler isolado.
/// </remarks>
public class CentralEntrarComoFalhaVisivelTests(WebAppFixture fixture) : IClassFixture<WebAppFixture>, IAsyncLifetime
{
    private const string SuperAdminEmail = RevendaApp.Web.Features.Auth.SeedData.DefaultEmail;
    private const string SuperAdminPassword = RevendaApp.Web.Features.Auth.SeedData.DefaultPassword;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Entrar_como_empresa_inativa_volta_a_central_com_mensagem()
    {
        var empresaId = await CriarEmpresaAsync("Empresa Inativa", ativo: false);

        using var client = fixture.CreateClient();
        await AutenticarComoSuperAdminAsync(client);

        var token = await ObterTokenAntiforgeryAsync(client);
        using var resposta = await PostEntrarComoAsync(client, token, empresaId);

        // Antes: 403 (página de erro, sem explicação). Agora: volta para a central com o motivo.
        Assert.Equal(HttpStatusCode.Redirect, resposta.StatusCode);
        Assert.Contains("/central", resposta.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Contains("erro=empresa-inativa", resposta.Headers.Location!.OriginalString, StringComparison.Ordinal);

        // A central renderiza a mensagem e o principal NÃO foi trocado (continua super admin).
        using var central = await client.GetAsync("/central?erro=empresa-inativa");
        Assert.Equal(HttpStatusCode.OK, central.StatusCode);
        var html = await central.Content.ReadAsStringAsync();
        Assert.Contains("Empresa desativada", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Entrando como", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entrar_como_empresa_sem_admin_ativo_volta_a_central_com_mensagem()
    {
        var empresaId = await CriarEmpresaAsync("Empresa Sem Admin", ativo: true);

        using var client = fixture.CreateClient();
        await AutenticarComoSuperAdminAsync(client);

        var token = await ObterTokenAntiforgeryAsync(client);
        using var resposta = await PostEntrarComoAsync(client, token, empresaId);

        Assert.Equal(HttpStatusCode.Redirect, resposta.StatusCode);
        Assert.Contains("erro=sem-admin", resposta.Headers.Location!.OriginalString, StringComparison.Ordinal);

        using var central = await client.GetAsync("/central?erro=sem-admin");
        var html = await central.Content.ReadAsStringAsync();
        Assert.Contains("administrador ativo", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Central_lista_as_empresas_e_mostra_o_botao_entrar_como()
    {
        await CriarEmpresaAsync("Empresa Visivel", ativo: true);

        using var client = fixture.CreateClient();
        await AutenticarComoSuperAdminAsync(client);

        using var central = await client.GetAsync("/central");
        Assert.Equal(HttpStatusCode.OK, central.StatusCode);

        var html = await central.Content.ReadAsStringAsync();

        // Controle positivo da tela: título, empresa listada e o form POST do "Entrar como"
        // (o mesmo endpoint da Task 9, com antiforgery e o id da empresa no corpo).
        Assert.Contains("Central", html, StringComparison.Ordinal);
        // Nome sem acento de propósito: o HTML serializa "í" como entidade (&#xED;) e a asserção
        // ficaria dependente do encoding, não do comportamento.
        Assert.Contains("Empresa Visivel", html, StringComparison.Ordinal);
        Assert.Contains("action=\"/central/entrar-como\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"empresaId\"", html, StringComparison.Ordinal);
    }

    /// <summary>Cria uma empresa real (sem administrador) no Postgres compartilhado.</summary>
    private async Task<Guid> CriarEmpresaAsync(string nome, bool ativo)
    {
        using var db = fixture.CreateDbContext();
        var empresa = new Empresa($"visivel-{Guid.NewGuid():N}", nome);
        if (!ativo)
        {
            empresa.Desativar();
        }

        db.Set<Empresa>().Add(empresa);
        await db.SaveChangesAsync();
        return empresa.Id;
    }

    private static async Task AutenticarComoSuperAdminAsync(HttpClient client)
    {
        using var loginPage = await client.GetAsync("/login");
        var html = await loginPage.Content.ReadAsStringAsync();

        var token = System.Text.RegularExpressions.Regex
            .Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]*)\"").Groups[1].Value;
        var handler = System.Text.RegularExpressions.Regex
            .Match(html, "name=\"_handler\" value=\"([^\"]*)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));
        Assert.False(string.IsNullOrEmpty(handler));

        var form = new Dictionary<string, string>
        {
            ["_handler"] = handler,
            ["__RequestVerificationToken"] = token,
            ["Input.Email"] = SuperAdminEmail,
            ["Input.Password"] = SuperAdminPassword,
            ["Input.RememberMe"] = "true",
        };

        using var resposta = await client.PostAsync("/login", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, resposta.StatusCode);
        Assert.Contains("/central", resposta.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    /// <summary>
    /// Token antiforgery lido de <c>/login</c> (SSR estático). O cookie antiforgery é o mesmo da
    /// sessão, então o token vale para os endpoints POST do app autenticado.
    /// </summary>
    private static async Task<string> ObterTokenAntiforgeryAsync(HttpClient client)
    {
        using var resposta = await client.GetAsync("/login");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var html = await resposta.Content.ReadAsStringAsync();
        var token = System.Text.RegularExpressions.Regex
            .Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]*)\"").Groups[1].Value;

        Assert.False(string.IsNullOrEmpty(token));
        return token;
    }

    private static Task<HttpResponseMessage> PostEntrarComoAsync(HttpClient client, string token, Guid empresaId) =>
        client.PostAsync("/central/entrar-como", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["empresaId"] = empresaId.ToString(),
            ["__RequestVerificationToken"] = token,
        }));
}