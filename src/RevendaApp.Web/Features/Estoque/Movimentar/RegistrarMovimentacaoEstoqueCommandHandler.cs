using Mediator;

using Microsoft.AspNetCore.Components.Authorization;

using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.Infrastructure;
using RevendaApp.Infrastructure.Tenancy;
using RevendaApp.Web.Common;
using RevendaApp.Web.Common.Authorization;

namespace RevendaApp.Web.Features.Estoque.Movimentar;

public sealed class RegistrarMovimentacaoEstoqueCommandHandler(AppDbContext db, ITenantContext tenant,
    AuthenticationStateProvider? authStateProvider = null, IUsuarioAutenticado? usuarioAutenticado = null)
    : ICommandHandler<RegistrarMovimentacaoEstoqueCommand, bool>
{
    public async ValueTask<bool> Handle(RegistrarMovimentacaoEstoqueCommand command, CancellationToken cancellationToken)
    {
        // Exige empresa selecionada (o produto é localizado pelo filtro global do tenant).
        _ = tenant.EmpresaIdRequerida("movimentar estoque");

        // Operação restrita a administradores (ver explicação em CadastrarProdutoCommandHandler).
        if (usuarioAutenticado is not null)
        {
            await usuarioAutenticado.RequerAdminAsync(cancellationToken);
        }

        var produto = await db.Produtos.FindAsync([command.ProdutoId], cancellationToken);
        if (produto is null)
        {
            return false;
        }

        // Produto com controle de vasilhame: a entrada (compra) exige entrega de vazios ao fornecedor.
        // A guarda real (disponíveis suficientes) é feita no switch; aqui apenas documenta a intenção.
        // Fluxo atual: comprar cheios consome o mesmo número de vazios do depósito.

        int vasilhamesMovidos = 0;
        switch (command.Tipo)
        {
            case TipoMovimentacaoEstoque.Entrada:
                // Compra: entrega N vazios ao fornecedor e recebe N cheios. Para produto com
                // controle, exige que existam vasilhames disponíveis para entregar.
                if (produto.ControlaVasilhame && produto.QuantidadeVasilhames < command.Quantidade)
                {
                    throw new InvalidOperationException(
                        $"Vasilhames disponíveis insuficientes para a compra de {command.Quantidade} '{produto.Nome}': há apenas {produto.QuantidadeVasilhames}. Registre entrada manual de vazios antes de comprar cheios.");
                }

                produto.RegistrarEntrada(command.Quantidade);

                if (produto.ControlaVasilhame)
                {
                    // Cada cheio comprado exige a entrega de um vazio ao fornecedor.
                    produto.RegistrarSaidaVasilhame(command.Quantidade);
                    vasilhamesMovidos = command.Quantidade;
                }

                break;
            case TipoMovimentacaoEstoque.Saida:
                produto.RegistrarSaida(command.Quantidade);

                if (produto.ControlaVasilhame)
                {
                    // Saída manual (não venda): o vazio do produto acompanha o cheio (sai junto),
                    // não volta ao depósito. Vendas lidam com a troca no RegistrarVendaCommandHandler.
                    vasilhamesMovidos = 0;
                }

                break;
            case TipoMovimentacaoEstoque.Ajuste:
                // Para ajuste, a quantidade do comando representa o novo total em estoque.
                produto.AjustarEstoque(command.Quantidade);
                break;
        }

        // Entrada manual no estoque é, na prática, a compra na distribuidora. Saída manual e ajuste não
        // têm origem comercial: ficam como Manual para não se confundirem com venda.
        var origem = command.Tipo == TipoMovimentacaoEstoque.Entrada
                    ? OrigemMovimentacaoEstoque.Compra
                    : OrigemMovimentacaoEstoque.Manual;

        var movimentacao = new MovimentacaoEstoque(command.ProdutoId, command.Tipo, command.Quantidade, command.Motivo, origem: origem);
        db.MovimentacoesEstoque.Add(movimentacao);

        var (usuario, _) = await UsuarioAtual.ObterAsync(authStateProvider, db, cancellationToken);
        var tipoHistorico = command.Tipo switch
        {
            TipoMovimentacaoEstoque.Entrada => TipoHistoricoEstoque.Entrada,
            TipoMovimentacaoEstoque.Saida => TipoHistoricoEstoque.Saida,
            _ => TipoHistoricoEstoque.Ajuste,
        };
        var descricao = command.Tipo switch
        {
            TipoMovimentacaoEstoque.Entrada => $"Entrada de {command.Quantidade} produto{(command.Quantidade == 1 ? string.Empty : "s")}",
            TipoMovimentacaoEstoque.Saida => $"Saída de {command.Quantidade} produto{(command.Quantidade == 1 ? string.Empty : "s")}",
            _ => $"Ajuste de estoque para {command.Quantidade}",
        };

        db.HistoricosEstoque.Add(new HistoricoEstoque(
            command.ProdutoId,
            tipoHistorico,
            descricao,
            command.Motivo,
            usuario,
            quantidade: command.Quantidade));

        // Produto que controla vasilhame: um registro adicional descreve o movimento dos vazios na
        // entrada (saída de vazios ao fornecedor, entregues junto com a compra dos cheios).
        if (vasilhamesMovidos > 0)
        {
            var descricaoVasilhame = command.Tipo switch
            {
                TipoMovimentacaoEstoque.Entrada => $"Entrega de {vasilhamesMovidos} vasilhame{(vasilhamesMovidos == 1 ? string.Empty : "s")} ao fornecedor na compra",
                _ => $"Movimentação de {vasilhamesMovidos} vasilhame{(vasilhamesMovidos == 1 ? string.Empty : "s")}",
            };

            db.HistoricosEstoque.Add(new HistoricoEstoque(
                command.ProdutoId,
                TipoHistoricoEstoque.Vasilhame,
                descricaoVasilhame,
                command.Motivo,
                usuario,
                quantidade: vasilhamesMovidos));
        }

        await db.SaveChangesAsync(cancellationToken);

        return true;
    }
}