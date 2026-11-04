using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace RevendaApp.IntegrationTests.Tests;

/// <summary>
/// As chaves de Data Protection precisam sobreviver ao restart do processo: sem persistência, cada
/// restart do container gera chaves novas e todo cookie de autenticação e token antiforgery
/// emitidos antes ficam inválidos (todos os usuários são deslogados). Com mais de uma réplica,
/// cada instância teria chaves diferentes e a sessão quebraria ao trocar de instância.
/// </summary>
/// <remarks>
/// A tabela é <b>global</b> (sem <c>EmpresaId</c>, sem filtro por tenant), então o contexto usado
/// aqui é o central (<see cref="DatabaseFixture.CreateDbContextCentral"/>) — o mesmo modo em que o
/// <c>PersistKeysToDbContext</c> grava em produção.
/// </remarks>
public class DataProtectionTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// A tabela existe de fato no schema (asserção positiva: a linha precisa aparecer em
    /// <c>information_schema.tables</c>).
    /// </summary>
    [Fact]
    public async Task Tabela_DataProtectionKeys_existe_no_schema()
    {
        using var db = fixture.CreateDbContextCentral();

        var tabelas = await db.Database
            .SqlQueryRaw<string>(
                """
                SELECT table_name AS "Value"
                FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = 'DataProtectionKeys'
                """)
            .ToListAsync();

        Assert.Equal(["DataProtectionKeys"], tabelas);
    }

    /// <summary>
    /// Ida e volta real: grava uma chave pelo contexto, salva e lê de volta em um contexto novo.
    /// É a condição para o <c>PersistKeysToDbContext</c> funcionar — se a tabela não aceitasse a
    /// escrita ou perdesse as colunas, o restart continuaria invalidando os cookies.
    /// </summary>
    [Fact]
    public async Task Chave_de_DataProtection_sobrevive_ida_e_volta_pelo_banco()
    {
        // Nome único por execução: o volume do Postgres é persistente e o reset da fixture não
        // esvazia esta tabela (ela não faz parte do conjunto de dados de domínio/Identity).
        var nomeAmigavel = $"chave-de-teste-hardening-{Guid.NewGuid():N}";
        const string xml = "<key id=\"chave-de-teste-hardening\" version=\"1\"><creationDate>2026-11-04T00:00:00Z</creationDate></key>";

        using (var db = fixture.CreateDbContextCentral())
        {
            db.DataProtectionKeys.Add(new DataProtectionKey
            {
                FriendlyName = nomeAmigavel,
                Xml = xml,
            });

            await db.SaveChangesAsync();
        }

        // Contexto NOVO: prova que a chave foi para o banco, não para o change tracker.
        using (var db = fixture.CreateDbContextCentral())
        {
            var chave = await db.DataProtectionKeys.SingleAsync(k => k.FriendlyName == nomeAmigavel);

            Assert.Equal(nomeAmigavel, chave.FriendlyName);
            Assert.Equal(xml, chave.Xml);
            Assert.True(chave.Id > 0);
        }
    }
}