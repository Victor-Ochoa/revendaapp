namespace RevendaApp.Domain.Entities;

/// <summary>
/// Registro de auditoria de uma ação relevante (edição de dados da empresa, criação/inativação de
/// empresa ou usuário, "entrar como", ...). É uma entidade <b>global</b>: não pertence
/// exclusivamente a uma empresa, porque a central precisa lê-la de todas e cada empresa precisa ler
/// as suas.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por isso <see cref="EmpresaId"/> é anulável e não há filtro global por empresa nem
/// preenchimento automático pelo interceptor</b>: <c>null</c> significa "ação da central" (super
/// admin sem empresa) e o valor é atribuído explicitamente por quem grava o registro.
/// </para>
/// <para>
/// <see cref="Acao"/> e <see cref="Origem"/> são <b>strings</b> (não enum) por decisão de design: a
/// lista de ações cresce a cada funcionalidade e o banco guarda o valor legível. Use as constantes
/// de <see cref="Acoes"/> e <see cref="Origens"/> para evitar literais soltos.
/// </para>
/// </remarks>
public class RegistroAuditoria
{
    /// <summary>Tamanho máximo de <see cref="Detalhes"/> (o resumo é truncado antes de gravar).</summary>
    public const int TamanhoMaximoDetalhes = 2000;

    /// <summary>
    /// Tamanho máximo de <see cref="UsuarioNome"/>. É o mesmo da coluna (200), mas <b>menor</b> que o
    /// limite de <c>AspNetUsers.Email</c> (256): o nome aqui é o e-mail do login, e um e-mail mais
    /// longo estouraria a coluna. Como a auditoria é gravada no <b>mesmo</b> <c>SaveChanges</c> da
    /// edição, isso derrubaria a edição inteira — então o nome é truncado (o <see cref="UsuarioId"/>
    /// continua sendo a identidade exata).
    /// </summary>
    public const int TamanhoMaximoUsuarioNome = 200;

    /// <summary>Rótulo usado quando não há usuário autenticado (seed, tarefa de sistema).</summary>
    public const string UsuarioSistema = "sistema";

    /// <summary>Ações auditadas. Cresce conforme novas funcionalidades gravam auditoria.</summary>
    public static class Acoes
    {
        /// <summary>O administrador da empresa editou os dados dela pela aba "Minha Empresa".</summary>
        public const string ConfigEmpresaEditada = "ConfigEmpresaEditada";

        /// <summary>O super admin criou uma empresa (e o primeiro administrador dela) pela central.</summary>
        public const string EmpresaCriada = "EmpresaCriada";

        /// <summary>O super admin editou os dados de cadastro de uma empresa pela central.</summary>
        public const string EmpresaEditada = "EmpresaEditada";

        /// <summary>O super admin inativou uma empresa pela central.</summary>
        public const string EmpresaInativada = "EmpresaInativada";

        /// <summary>O super admin reativou uma empresa pela central.</summary>
        public const string EmpresaReativada = "EmpresaReativada";

        /// <summary>O super admin criou um usuário numa empresa pela central.</summary>
        public const string UsuarioCriado = "UsuarioCriado";

        /// <summary>O super admin inativou um usuário de uma empresa pela central.</summary>
        public const string UsuarioDesativado = "UsuarioDesativado";

        /// <summary>O super admin reativou um usuário de uma empresa pela central.</summary>
        public const string UsuarioReativado = "UsuarioReativado";

        /// <summary>O super admin entrou como administrador de uma empresa (impersonation).</summary>
        public const string EntrarComo = "EntrarComo";

        /// <summary>O super admin encerrou a impersonation e voltou à central.</summary>
        public const string VoltarCentral = "VoltarCentral";

        /// <summary>Normalização única da quitação de fiado legado, executada no bootstrap.</summary>
        public const string FiadoLegadoNormalizado = "FiadoLegadoNormalizado";
    }

    /// <summary>Origem do registro: de onde a ação partiu.</summary>
    public static class Origens
    {
        /// <summary>Ação executada dentro do app da empresa.</summary>
        public const string App = "App";

        /// <summary>Ação executada pela central do super admin.</summary>
        public const string Central = "Central";

        /// <summary>Ação executada pelo próprio sistema no bootstrap (sem usuário).</summary>
        public const string Sistema = "Sistema";
    }

    public Guid Id { get; private set; }

    /// <summary>Empresa afetada; <c>null</c> = ação da central (sem empresa selecionada).</summary>
    public Guid? EmpresaId { get; private set; }

    /// <summary>Id do usuário autenticado que executou a ação; <c>null</c> quando não resolvido.</summary>
    public Guid? UsuarioId { get; private set; }

    /// <summary>Nome (e-mail) do usuário autenticado; "sistema" quando não autenticado.</summary>
    public string UsuarioNome { get; private set; } = UsuarioSistema;

    /// <summary>Ação executada — uma das constantes de <see cref="Acoes"/>.</summary>
    public string Acao { get; private set; } = string.Empty;

    /// <summary>Resumo legível do que mudou (em pt-BR, com "antes → depois"); truncado em 2000 chars.</summary>
    public string Detalhes { get; private set; } = string.Empty;

    /// <summary>Momento da ação, em UTC.</summary>
    public DateTime DataHora { get; private set; }

    /// <summary>Origem da ação — uma das constantes de <see cref="Origens"/>.</summary>
    public string Origem { get; private set; } = string.Empty;

    /// <summary>Construtor exigido pelo EF Core.</summary>
    private RegistroAuditoria()
    {
    }

    public RegistroAuditoria(
        Guid? empresaId,
        Guid? usuarioId,
        string? usuarioNome,
        string acao,
        string detalhes,
        string origem)
    {
        if (string.IsNullOrWhiteSpace(acao))
        {
            throw new ArgumentException("A ação auditada é obrigatória.", nameof(acao));
        }

        if (string.IsNullOrWhiteSpace(origem))
        {
            throw new ArgumentException("A origem do registro de auditoria é obrigatória.", nameof(origem));
        }

        Id = Guid.NewGuid();
        EmpresaId = empresaId;
        UsuarioId = usuarioId;
        UsuarioNome = Truncar(
            string.IsNullOrWhiteSpace(usuarioNome) ? UsuarioSistema : usuarioNome.Trim(),
            TamanhoMaximoUsuarioNome);
        Acao = acao.Trim();
        Detalhes = Truncar(detalhes, TamanhoMaximoDetalhes);
        DataHora = DateTime.UtcNow;
        Origem = origem.Trim();
    }

    /// <summary>
    /// Corta o texto no limite da coluna. O corte é feito aqui (e não só no handler) para que nenhum
    /// caminho consiga gravar um texto maior do que a coluna aceita.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Se o corte cair no <b>meio de um par surrogate</b> (ex.: um emoji, que ocupa 2 chars UTF-16),
    /// recua um caractere: meio par é texto inválido e o Postgres recusa a gravação.
    /// </para>
    /// <para>
    /// <b>Público de propósito:</b> quem monta um resumo antes de construir o registro (ex.: o
    /// handler de "Minha Empresa") precisa do MESMO corte. Cortar antes, com <c>texto[..2000]</c>
    /// cru, partiria o par e o guarda da entidade não teria como consertar (o texto já chegaria com
    /// tamanho válido).
    /// </para>
    /// </remarks>
    public static string Truncar(string? texto, int limite)
    {
        if (string.IsNullOrEmpty(texto) || texto.Length <= limite)
        {
            return texto ?? string.Empty;
        }

        var tamanho = limite;
        if (char.IsHighSurrogate(texto[tamanho - 1]))
        {
            tamanho--;
        }

        return texto[..tamanho];
    }
}