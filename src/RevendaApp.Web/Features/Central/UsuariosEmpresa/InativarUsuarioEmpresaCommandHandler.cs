using System.Data;

using Mediator;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

using RevendaApp.Domain.Entities;
using RevendaApp.Infrastructure;
using RevendaApp.Infrastructure.Identity;
using RevendaApp.Infrastructure.Tenancy;
using RevendaApp.Web.Common;

namespace RevendaApp.Web.Features.Central.UsuariosEmpresa;

/// <summary>
/// Inativa/reativa um usuário de uma empresa pela central.
/// </summary>
/// <remarks>
/// <para>
/// <b>Escopo do tenant:</b> o usuário é carregado com <c>EmpresaId == command.EmpresaId</c> na
/// própria consulta. Um id de usuário de outra empresa simplesmente não é encontrado — o handler
/// nunca aceita um <c>UsuarioId</c> avulso.
/// </para>
/// <para>
/// <b>Último administrador (atômico):</b> inativar o último <c>Admin</c> ativo deixaria a empresa
/// sem quem a administre. A contagem e a gravação rodam na <b>mesma transação</b>, em nível
/// <see cref="IsolationLevel.Serializable"/>: sob <c>ReadCommitted</c> duas desativações
/// simultâneas dos dois únicos admins passariam as duas (check-then-act clássico) e a empresa
/// ficaria sem administrador. O conflito de serialização faz uma das transações falhar e ser
/// reexecutada pela execution strategy, que então enxerga o estado novo e recusa.
/// </para>
/// <para>
/// <b>Revogação da sessão viva:</b> ao <b>inativar</b>, o <c>SecurityStamp</c> é renovado via
/// <see cref="UserManager{TUser}.UpdateSecurityStampAsync"/>. Sem isso, o cookie já emitido
/// continuaria válido até o <c>SecurityStampValidator</c> rodar (30 min) ou o navegador fechar: um
/// usuário recém-desativado seguiria operando. A reativação <b>não</b> mexe no stamp (o usuário
/// simplesmente volta a poder logar).
/// </para>
/// <para>
/// <b>Desativação da empresa não é cascata:</b> inativar a <b>empresa</b> não renova o stamp de
/// ninguém de propósito — reativar precisa devolver o acesso a todos sem uma segunda operação, e o
/// bloqueio de login já é do <c>LoginCommandHandler</c>.
/// </para>
/// </remarks>
public sealed partial class InativarUsuarioEmpresaCommandHandler(
    AppDbContext db,
    ITenantContext tenant,
    UserManager<ApplicationUser> userManager,
    AuthenticationStateProvider? authStateProvider = null,
    ILogger<InativarUsuarioEmpresaCommandHandler>? logger = null)
    : ICommandHandler<InativarUsuarioEmpresaCommand, InativarUsuarioEmpresaResult>
{
    /// <summary>
    /// Mensagem da inativação que <b>commitou</b> mas não conseguiu revogar a sessão viva do
    /// usuário (falha ou exceção ao renovar o <c>SecurityStamp</c>). O operador precisa saber que
    /// o acesso ainda pode estar ativo — por isso não é uma falha silenciosa.
    /// </summary>
    private const string MensagemSessaoNaoRevogada =
        "Usuário inativado, mas não foi possível revogar a sessão ativa dele. "
        + "Inative-o novamente ou peça para ele sair do sistema.";

    public async ValueTask<InativarUsuarioEmpresaResult> Handle(InativarUsuarioEmpresaCommand command, CancellationToken cancellationToken)
    {
        // Limite real de segurança: a política da rota não basta, o handler reforça no servidor.
        if (!tenant.EhSuperAdmin)
        {
            throw new InvalidOperationException("Somente o super administrador da central pode inativar usuários de uma empresa.");
        }

        var resultado = await TransacaoComRetry.ExecutarAsync(
            db,
            transacao => ExecutarNaTransacaoAsync(command, transacao, cancellationToken),
            cancellationToken,
            nivelIsolamento: command.Ativo ? null : IsolationLevel.Serializable);

        if (!resultado.Sucesso)
        {
            return resultado;
        }

        // Fora da transação: o UpdateSecurityStampAsync faz o próprio SaveChanges no UserManager.
        // Só na inativação — reativar não precisa (e não deve) derrubar sessão de ninguém.
        if (!command.Ativo)
        {
            var usuario = await db.Users.FirstAsync(
                u => u.Id == command.UsuarioId && u.EmpresaId == command.EmpresaId, cancellationToken);

            // O resultado NÃO pode ser descartado: se o stamp não for atualizado, a sessão viva do
            // usuário continua válida e a inativação (já commitada) não revogou o acesso — que é
            // exatamente o objetivo deste passo. Reportar a falha é obrigatório.
            // A inativação já está commitada; uma exceção aqui derrubaria o circuito do painel sem
            // informar nada. Converte em falha explícita para o operador ver o que fazer.
            try
            {
                var atualizacaoStamp = await userManager.UpdateSecurityStampAsync(usuario);

                if (!atualizacaoStamp.Succeeded)
                {
                    // O Identity recusou a renovação (sem exceção): a trilha de log diz quais erros
                    // foram esses — sem ela o suporte só tem a mensagem genérica do operador.
                    if (logger is not null)
                    {
                        LogRenovacaoStampRecusada(
                            logger,
                            command.UsuarioId,
                            command.EmpresaId,
                            string.Join("; ", atualizacaoStamp.Errors.Select(erro => erro.Description)));
                    }

                    return InativarUsuarioEmpresaResult.Falha(MensagemSessaoNaoRevogada);
                }
            }
            catch (Exception excecao) when (excecao is not OperationCanceledException)
            {
                // A inativação JÁ está commitada; falhar aqui não a desfaz. O log é a única trilha
                // de por que a sessão do usuário continua viva.
                if (logger is not null)
                {
                    LogFalhaAoRenovarStamp(logger, excecao, command.UsuarioId, command.EmpresaId);
                }

                return InativarUsuarioEmpresaResult.Falha(MensagemSessaoNaoRevogada);
            }
        }

        return resultado;
    }

    private async Task<InativarUsuarioEmpresaResult> ExecutarNaTransacaoAsync(
        InativarUsuarioEmpresaCommand command,
        IDbContextTransaction transacao,
        CancellationToken cancellationToken)
    {
        // A entidade foi carregada na tentativa anterior (retry): recomeça limpo.
        db.ChangeTracker.Clear();

        // Filtro por empresa na própria consulta: id de usuário de outra empresa não é encontrado.
        var usuario = await db.Users
            .FirstOrDefaultAsync(u => u.Id == command.UsuarioId && u.EmpresaId == command.EmpresaId, cancellationToken);

        if (usuario is null)
        {
            return InativarUsuarioEmpresaResult.Falha("Usuário não encontrado nesta empresa.");
        }

        if (!command.Ativo && await EhUltimoAdminAtivoAsync(command.EmpresaId, usuario.Id, cancellationToken))
        {
            return InativarUsuarioEmpresaResult.Falha(
                "Não é possível inativar o último administrador ativo da empresa. "
                + "Crie ou reative outro administrador antes.");
        }

        var papel = (await userManager.GetRolesAsync(usuario)).FirstOrDefault() ?? "(sem papel)";

        usuario.Ativo = command.Ativo;

        // Auditoria na MESMA transação: se o commit falhar, a alteração e o registro somem juntos
        // (e o retry recomeça do zero, sem duplicar o registro).
        var (usuarioNome, autorId) = await UsuarioAtual.ObterAsync(authStateProvider, db, cancellationToken);
        var acao = command.Ativo
            ? RegistroAuditoria.Acoes.UsuarioReativado
            : RegistroAuditoria.Acoes.UsuarioDesativado;
        var estado = command.Ativo ? "reativado" : "inativado";
        var detalhes = $"Usuário {usuario.Email} ({usuario.Nome}) com o papel {papel} {estado}.";

        db.RegistrosAuditoria.Add(new RegistroAuditoria(
            command.EmpresaId,
            autorId,
            usuarioNome,
            acao,
            detalhes,
            RegistroAuditoria.Origens.Central));

        await db.SaveChangesAsync(cancellationToken);
        await transacao.CommitAsync(cancellationToken);

        return InativarUsuarioEmpresaResult.Ok();
    }

    /// <summary>
    /// Verdadeiro quando o usuário é <c>Admin</c> e não existe nenhum outro <c>Admin</c> ativo na
    /// mesma empresa. Usuários de empresa não têm <c>SuperAdmin</c>, então a contagem considera só
    /// a role <c>Admin</c>. O join é restrito aos usuários <b>da empresa</b> (não varre as demais).
    /// </summary>
    private async Task<bool> EhUltimoAdminAtivoAsync(Guid empresaId, Guid usuarioId, CancellationToken cancellationToken)
    {
        var ehAdmin = await (from ur in db.Set<IdentityUserRole<Guid>>()
                             join r in db.Set<ApplicationRole>() on ur.RoleId equals r.Id
                             join u in db.Users on ur.UserId equals u.Id
                             where u.EmpresaId == empresaId && ur.UserId == usuarioId && r.Name == ApplicationRole.Admin
                             select ur.UserId)
            .AnyAsync(cancellationToken);

        if (!ehAdmin)
        {
            // Não é administrador: inativar não deixa a empresa sem admin.
            return false;
        }

        var outrosAdminsAtivos = await (from ur in db.Set<IdentityUserRole<Guid>>()
                                        join r in db.Set<ApplicationRole>() on ur.RoleId equals r.Id
                                        join u in db.Users on ur.UserId equals u.Id
                                        where u.EmpresaId == empresaId
                                            && u.Ativo
                                            && u.Id != usuarioId
                                            && r.Name == ApplicationRole.Admin
                                        select u.Id)
            .AnyAsync(cancellationToken);

        return !outrosAdminsAtivos;
    }

    /// <summary>
    /// Trilha de diagnóstico da revogação de sessão. Fica fora do <c>Handle</c> porque o
    /// <c>LoggerMessage</c> gerado é o caminho recomendado pelo analisador (CA1848): evita boxing e
    /// formatação quando o nível não está habilitado.
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Identity recusou a renovação do SecurityStamp do usuário {UsuarioId} da empresa {EmpresaId}: {Erros}")]
    private static partial void LogRenovacaoStampRecusada(
        ILogger logger,
        Guid usuarioId,
        Guid empresaId,
        string erros);

    /// <summary>Mesma trilha, para a exceção que escapa do <c>UpdateSecurityStampAsync</c>.</summary>
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Falha ao renovar o SecurityStamp do usuário {UsuarioId} da empresa {EmpresaId} após inativação.")]
    private static partial void LogFalhaAoRenovarStamp(
        ILogger logger,
        Exception excecao,
        Guid usuarioId,
        Guid empresaId);
}