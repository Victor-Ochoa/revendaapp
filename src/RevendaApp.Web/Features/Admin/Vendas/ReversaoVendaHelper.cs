using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.Infrastructure;

namespace RevendaApp.Web.Features.Admin.Vendas;

/// <summary>
/// Reverte os efeitos de uma venda no estoque e no saldo devedor do cliente, sem apagá-la do
/// banco (a exclusão é lógica via <see cref="Venda.Excluir"/>). Usado pelos handlers de
/// exclusão e edição de venda.
/// </summary>
public static class ReversaoVendaHelper
{
    /// <summary>
    /// Desfaz o que a venda causou: devolve a quantidade vendida ao estoque de cada produto
    /// (revertendo a <see cref="MovimentacaoEstoque"/> original), remove os históricos de estoque
    /// vinculados à venda e, se for fiado, abate o valor total da venda do saldo devedor.
    /// Também remove o <see cref="Pagamento"/> gerado no ato quando a venda não é fiado
    /// (pagamento vinculado à venda), para não somar dinheiro de uma venda desfeita.
    /// </summary>
    public static async Task ReverterAsync(AppDbContext db, Venda venda, CancellationToken cancellationToken)
    {
        // Devolve o estoque vendido de cada produto (estorno). As movimentações de saída originais
        // NÃO são removidas (elas têm sem apenas o ProdutoId/Motivo, sem vendaId, e remover por
        // match apagaria saídas de outras vendas ativas). Em vez disso, registra uma movimentação
        // de entrada de reversão por item, mantendo a trilha de auditoria completa.
        var produtos = await db.Produtos
            .Where(p => venda.Itens.Select(i => i.ProdutoId).Contains(p.Id))
            .ToListAsync(cancellationToken);

        foreach (var item in venda.Itens)
        {
            var produto = produtos.FirstOrDefault(p => p.Id == item.ProdutoId);
            if (produto is null)
            {
                // Produto removido do catálogo: não há estoque a devolver nem FK para movimentar.
                continue;
            }

            produto.RegistrarEntrada(item.Quantidade);
            db.MovimentacoesEstoque.Add(new MovimentacaoEstoque(
                item.ProdutoId,
                TipoMovimentacaoEstoque.Entrada,
                item.Quantidade,
                "Estorno de venda excluída",
                origem: OrigemMovimentacaoEstoque.EstornoVenda));

            // Reverte o efeito de vasilhame que a venda causou no item. Desde a migração
            // AdicionarVendaIdEmprestimosVasilhames, o empréstimo criado por venda carrega o
            // VendaId — a reversão desfaz EXATAMENTE o que a venda criou, por venda:
            // - Empréstimo (produto que controla vasilhame): remove as linhas abertas DESTA venda
            //   (EmprestimoVasilhame.VendaId == venda.Id) e decrementa o contador pelo removido.
            //   Se a venda não tem linhas atribuíveis (venda legada/backfill, ou devolução manual
            //   já quitou o empréstimo), faz no-op seguro — não lança nem decrementa duplo.
            // - Troca (produto que controla vasilhame): a venda adicionou Vasilhames + qtd. Se a
            //   venda realmente adicionou (venda de troca criada com controle), remove qtd; senão
            //   (venda legada pré-backfill, ou venda "defensiva" sem controle) no-op.
            if (item.EhEmprestimo)
            {
                if (produto.ControlaVasilhame)
                {
                    await DesfazerEmprestimosDaVendaAsync(
                        db, venda.ClienteId, item.ProdutoId, venda.Id, produto, cancellationToken);
                }
            }
            else if (produto.ControlaVasilhame)
            {
                // A troca devolveu o vazio ao depósito na venda; ao reverter, o vazio sai de novo.
                // PORÉM: só reverter se a venda REALMENTE adicionou vazios — verificado pelo
                // histórico "Vasilhame" com vendaId que o RegistrarVendaCommandHandler grava na
                // troca com controle. Vendas legadas (pré-backfill/seed) fazem RegistrarSaida
                // sem adicionar vazios — para elas, a reversão NÃO deve mexer em Vasilhames (senão
                // decrementa vazios que a venda nunca criou). Clamp no disponível por segurança.
                var foiTrocaComControle = await db.HistoricosEstoque.AnyAsync(
                    h => h.VendaId == venda.Id
                        && h.ProdutoId == item.ProdutoId
                        && h.Tipo == TipoHistoricoEstoque.Vasilhame,
                    cancellationToken);

                if (foiTrocaComControle)
                {
                    var reverterTroca = Math.Min(item.Quantidade, produto.QuantidadeVasilhames);
                    if (reverterTroca > 0)
                    {
                        produto.RegistrarSaidaVasilhame(reverterTroca);
                    }
                }
            }
        }

        // História de estoque vinculada à venda (visualização) também é revertida.
        db.HistoricosEstoque.RemoveRange(
            db.HistoricosEstoque.Where(h => h.VendaId == venda.Id));

        if (venda.FormaPagamento == FormaPagamento.Fiado)
        {
            var cliente = await db.Clientes.FindAsync([venda.ClienteId], cancellationToken);
            if (cliente is not null && venda.SaldoFiado > 0m)
            {
                // Estorna apenas o que a venda ainda devia: se houve pagamento parcial já alocado,
                // devolver o ValorTotal inteiro criaria crédito que o cliente nunca pagou (e o
                // clamp em zero de RegistrarPagamento esconderia o erro quando ele não tivesse
                // outra dívida). Venda já quitada tem SaldoFiado == 0 e RegistrarPagamento lança
                // com valor <= 0.
                cliente.RegistrarPagamento(venda.SaldoFiado);
            }
        }
        else
        {
            // Venda não-fiado gerou um Pagamento no ato (vínculo Pagamento.VendaId). Remove para
            // não contabilizar um recebimento de uma venda desfeita.
            db.Pagamentos.RemoveRange(
                db.Pagamentos.Where(p => p.VendaId == venda.Id));
        }
    }

    /// <summary>
    /// Desfaz o efeito de empréstimo que a venda criou: remove as linhas de
    /// <see cref="EmprestimoVasilhame"/> abertas DESTA venda (<c>VendaId == vendaId</c>) e
    /// decrementa o contador do produto pelo <see cref="EmprestimoVasilhame.Saldo"/> removido.
    /// Se a venda não tem linhas atribuíveis abertas (venda legada pré-VendaId, ou devolução manual
    /// já quitou o empréstimo — <c>DevolvidoEm</c> set), faz no-op seguro: a devolução manual já
    /// reduziu o contador, então decrementar de novo seria duplo. Nunca lança por "linhas
    /// insuficientes" (evita sujar o DbContext scoped após mutação parcial).
    /// </summary>
    private static async Task DesfazerEmprestimosDaVendaAsync(
        AppDbContext db, Guid clienteId, Guid produtoId, Guid vendaId, Produto produto, CancellationToken cancellationToken)
    {
        // Linhas abertas criadas por esta venda (somente abertas — quitadas por devolução manual
        // já saíram do contador).
        var daVenda = await db.EmprestimosVasilhames
            .Where(e => e.ClienteId == clienteId
                && e.ProdutoId == produtoId
                && e.VendaId == vendaId
                && e.DevolvidoEm == null)
            .OrderBy(e => e.Data)
            .ToListAsync(cancellationToken);

        var removido = 0;
        foreach (var emprestimo in daVenda)
        {
            db.EmprestimosVasilhames.Remove(emprestimo);
            removido += emprestimo.Saldo;
        }

        // Decrementa o contador pelo que foi realmente removido (≤ quantidade). Se nada foi
        // removido (venda legada/quitada), no-op — sem decremento duplo e sem lançar.
        if (removido > 0)
        {
            produto.RegistrarDevolucaoVasilhame(removido);
        }
    }
}