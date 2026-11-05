using Mediator;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.Infrastructure;
using RevendaApp.Infrastructure.Tenancy;
using RevendaApp.Web.Common;
using RevendaApp.Web.Common.Authorization;

namespace RevendaApp.Web.Features.Clientes.Emprestimos;

public sealed class RegistrarDevolucaoVasilhameCommandHandler(AppDbContext db, ITenantContext tenant,
    TimeProvider timeProvider, AuthenticationStateProvider? authStateProvider = null,
    IUsuarioAutenticado? usuarioAutenticado = null)
    : ICommandHandler<RegistrarDevolucaoVasilhameCommand, bool>
{
    public async ValueTask<bool> Handle(RegistrarDevolucaoVasilhameCommand command, CancellationToken cancellationToken)
    {
        // Exige empresa selecionada (produto/cliente localizados pelo filtro global do tenant).
        _ = tenant.EmpresaIdRequerida("registrar devolução de vasilhame");

        // Instante único da operação: garante que todos os empréstimos quitados nesta devolução
        // registrem exatamente o mesmo momento de auditoria.
        var agora = timeProvider.GetUtcNow().UtcDateTime;

        // Operação restrita a administradores (mesmo padrão dos handlers de mutação de estoque).
        if (usuarioAutenticado is not null)
        {
            await usuarioAutenticado.RequerAdminAsync(cancellationToken);
        }

        var produto = await db.Produtos.FindAsync([command.ProdutoId], cancellationToken)
            ?? throw new InvalidOperationException("Produto não encontrado.");

        // Guarda de integridade: o cliente só pode devolver o que ainda tem em aberto para esse
        // produto (soma dos saldos dos empréstimos não quitados). Validada antes de qualquer
        // mutação para não deixar estado parcial.
        var saldoEmAberto = await db.EmprestimosVasilhames
            .Where(e => e.ClienteId == command.ClienteId
                        && e.ProdutoId == command.ProdutoId
                        && e.DevolvidoEm == null)
            .SumAsync(e => (int?)(e.Quantidade - e.QuantidadeDevolvida), cancellationToken) ?? 0;

        if (saldoEmAberto < command.Quantidade)
        {
            throw new InvalidOperationException("Quantidade devolvida não pode exceder os vasilhames emprestados");
        }

        // Regra 1 — quita do saldo em aberto do cliente, sempre: reduz os emprestados do produto
        // e consome a devolução contra os empréstimos mais antigos (FIFO por Data).
        produto.RegistrarDevolucaoVasilhame(command.Quantidade);

        var emAbertoFifo = await db.EmprestimosVasilhames
            .Where(e => e.ClienteId == command.ClienteId
                        && e.ProdutoId == command.ProdutoId
                        && e.DevolvidoEm == null)
            .OrderBy(e => e.Data)
            .ToListAsync(cancellationToken);

        var restante = command.Quantidade;
        foreach (var emprestimo in emAbertoFifo)
        {
            if (restante <= 0)
            {
                break;
            }

            var aQuitar = Math.Min(restante, emprestimo.Saldo);
            emprestimo.RegistrarDevolucao(aQuitar, agora);
            restante -= aQuitar;
        }

        // Regra 2 — a devolução devolve o VASILHAME (vazio) ao depósito de vazios disponíveis:
        // Emprestados − (já feito acima) e Vasilhames disponíveis +. Não mexe em Estoque cheio — o vazio
        // devolvido fica pronto para ser emprestado de novo, vendido em troca ou entregue ao fornecedor.
        if (produto.ControlaVasilhame)
        {
            produto.RegistrarEntradaVasilhame(command.Quantidade);
        }

        var (usuario, _) = await UsuarioAtual.ObterAsync(authStateProvider, db, cancellationToken);
        var descricao = command.Quantidade == 1
            ? "Devolução de 1 vasilhame devolvido ao estoque de vazios disponíveis"
            : $"Devolução de {command.Quantidade} vasilhames devolvidos ao estoque de vazios disponíveis";

        db.HistoricosEstoque.Add(new HistoricoEstoque(
            produto.Id,
            TipoHistoricoEstoque.DevolucaoVasilhame,
            descricao,
            command.Observacao ?? "Devolução manual",
            usuario,
            quantidade: command.Quantidade));

        await db.SaveChangesAsync(cancellationToken);

        return true;
    }
}