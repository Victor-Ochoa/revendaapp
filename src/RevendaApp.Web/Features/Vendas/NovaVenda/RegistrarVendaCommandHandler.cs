using Mediator;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

using RevendaApp.Domain.Entities;
using RevendaApp.Domain.Enums;
using RevendaApp.Infrastructure;
using RevendaApp.Infrastructure.Tenancy;
using RevendaApp.Web.Common;

namespace RevendaApp.Web.Features.Vendas.NovaVenda;

public sealed class RegistrarVendaCommandHandler(AppDbContext db, ITenantContext tenant,
    AuthenticationStateProvider? authStateProvider = null)
    : ICommandHandler<RegistrarVendaCommand, RegistrarVendaResult>
{
    /// <summary>
    /// Mensagem exibida quando outra operação alterou o mesmo produto/cliente entre a leitura e a
    /// gravação (venda simultânea do último item, pagamento concorrente). Recarregar e repetir é a
    /// ação correta do operador.
    /// </summary>
    public const string MensagemConflito =
        "Os dados deste produto ou cliente foram alterados por outra operação enquanto você finalizava. "
        + "Recarregue a tela e tente novamente.";

    public async ValueTask<RegistrarVendaResult> Handle(RegistrarVendaCommand command, CancellationToken cancellationToken)
    {
        // Exige empresa selecionada. A guarda fica só aqui (entrada): o helper RegistrarAsync é
        // chamado também pela edição de venda, que já guarda no próprio entry handler.
        _ = tenant.EmpresaIdRequerida("registrar venda");

        var (usuario, vendedorId) = await UsuarioAtual.ObterAsync(authStateProvider, db, cancellationToken);
        var resultado = await RegistrarAsync(db, command, vendedorId, usuario, cancellationToken);

        // Persiste a venda (itens, movimentações, pagamento e histórico). O token `xmin` de
        // Produto/Cliente detecta que outra operação gravou a mesma linha depois da nossa leitura —
        // nesse caso a operação inteira é recusada em vez de gravar estoque/saldo inconsistentes.
        // A execution strategy do Npgsql não reexecuta este erro (não é transitório): recarregar e
        // repetir exige reconstruir o estado da operação, então o operador é orientado a refazer.
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException(MensagemConflito);
        }

        // Recalcula a recorrência de recompra dos produtos com aviso (após persistir: o motor
        // consulta o banco e só enxerga a venda recém-criada depois do save acima).
        foreach (var produtoId in command.Itens.Select(i => i.ProdutoId).Distinct())
        {
            var produto = await db.Produtos.FindAsync([produtoId], cancellationToken);
            if (produto is { AvisarRecompra: true })
            {
                await RecorrenciaCompraAprendizado.RecalcularAsync(db, resultado.ClienteId, produtoId, cancellationToken);
            }
        }

        // Segundo save para persistir as linhas de RecorrenciasCompra criadas/atualizadas/removidas.
        await db.SaveChangesAsync(cancellationToken);

        return resultado;
    }

    /// <summary>
    /// Registra uma venda no <paramref name="db"/> **sem persistir** (não chama
    /// <c>SaveChangesAsync</c>). Usado pelo fluxo de Nova Venda (que persiste em seguida) e pela
    /// edição de venda (que precisa re-criar a venda numa transação com a reversão dos efeitos
    /// antigos, sem salvar no meio). Centraliza as regras de negócio: preço por forma de
    /// pagamento, preço personalizado por cliente, Gás do Povo, desconto, estoque, histórico e
    /// pagamento de venda paga no ato. O <paramref name="dataHora"/> opcional define a data/hora
    /// da venda e dos empréstimos de vasilhame (usado pela edição para preservar a data original);
    /// a Nova Venda omite e usa <c>UtcNow</c>.
    /// </summary>
    internal static async Task<RegistrarVendaResult> RegistrarAsync(
        AppDbContext db,
        RegistrarVendaCommand command,
        Guid? vendedorId,
        string usuario,
        CancellationToken cancellationToken,
        DateTime? dataHora = null)
    {
        // Cliente existente (selecionado no autocomplete) ou novo cadastro rápido.
        Cliente cliente;
        if (command.ClienteId is Guid clienteId)
        {
            cliente = await db.Clientes.FindAsync([clienteId], cancellationToken)
                ?? throw new InvalidOperationException("O cliente selecionado não existe mais.");
        }
        else
        {
            var telefoneNormalizado = Cliente.NormalizarTelefone(command.TelefoneClienteNovo!);
            var telefoneJaExiste = await db.Clientes.AnyAsync(c => c.Telefone == telefoneNormalizado, cancellationToken);
            if (telefoneJaExiste)
            {
                throw new InvalidOperationException("Já existe um cliente com este telefone. Busque-o pelo nome na tela de venda.");
            }

            cliente = new Cliente(command.NomeClienteNovo!, command.TelefoneClienteNovo!, command.EnderecoClienteNovo!);
            db.Clientes.Add(cliente);
        }

        var venda = new Venda(cliente.Id, command.FormaPagamento, dataHora);
        venda.DefinirVendedor(vendedorId);

        foreach (var itemInput in command.Itens)
        {
            var produto = await db.Produtos.FindAsync([itemInput.ProdutoId], cancellationToken)
                ?? throw new InvalidOperationException($"Produto '{itemInput.ProdutoId}' não encontrado.");

            if (!produto.Ativo)
            {
                throw new InvalidOperationException($"O produto '{produto.Nome}' está desabilitado e não pode ser vendido.");
            }

            var precoBase = produto.PrecoParaFormaPagamento(command.FormaPagamento);

            // O preço personalizado (acordo cliente+produto) não se aplica a "Gás do Povo", que usa
            // sempre o preço subsidiado do produto.
            decimal precoUnitario;
            if (command.FormaPagamento == FormaPagamento.GasDoPovo)
            {
                precoUnitario = precoBase;
            }
            else if (itemInput.PrecoUnitario is decimal precoInformado)
            {
                // O preço exibido/confirmado na tela (personalizado do cliente ou editado pelo operador).
                precoUnitario = precoInformado;
                await AtualizarAcordoPrecoAsync(db, cliente.Id, produto.Id, precoInformado, precoBase, cancellationToken);
            }
            else
            {
                // Sem preço informado: usa o preço padrão (comportamento atual) e não mexe no acordo.
                precoUnitario = precoBase;
            }

            venda.AdicionarItem(produto.Id, itemInput.Quantidade, precoUnitario, produto.PrecoCusto, ehEmprestimo: itemInput.EhEmprestimo);

            if (itemInput.EhEmprestimo)
            {
                if (!produto.ControlaVasilhame)
                {
                    // Defensivo: o Validator impede empréstimo em produto sem controle de vasilhame;
                    // se chegar aqui, trata como troca normal (apenas Estoque−), sem mexer em vazios.
                    produto.RegistrarSaida(itemInput.Quantidade);
                }
                else
                {
                    // Empréstimo: entrega o cheio e anota o vazio como emprestado ao cliente. O
                    // estoque cheio pode ficar negativo quando o déficit é coberto por vasilhames
                    // já emprestados ao cliente (gate Estoque + Emprestados do cliente ≥ Qtd,
                    // validado no Validator).
                    produto.RegistrarSaidaEmprestimo(itemInput.Quantidade);
                    produto.RegistrarEmprestimoVasilhame(itemInput.Quantidade);
                    db.EmprestimosVasilhames.Add(new EmprestimoVasilhame(
                        cliente.Id, produto.Id, itemInput.Quantidade,
                        data: dataHora ?? DateTime.UtcNow, observacao: null, vendaId: venda.Id));
                    db.HistoricosEstoque.Add(new HistoricoEstoque(
                        produto.Id,
                        TipoHistoricoEstoque.EmprestimoVasilhame,
                        itemInput.Quantidade == 1
                            ? "Empréstimo de 1 vasilhame ao cliente"
                            : $"Empréstimo de {itemInput.Quantidade} vasilhames ao cliente",
                        HistoricoEstoque.MotivoVenda,
                        usuario,
                        quantidade: itemInput.Quantidade,
                        vendaId: venda.Id,
                        data: dataHora));
                }
            }
            else
            {
                produto.RegistrarSaida(itemInput.Quantidade);

                if (produto.ControlaVasilhame)
                {
                    // Troca: o vazio volta ao depósito.
                    produto.RegistrarEntradaVasilhame(itemInput.Quantidade);
                    db.HistoricosEstoque.Add(new HistoricoEstoque(
                        produto.Id,
                        TipoHistoricoEstoque.Vasilhame,
                        itemInput.Quantidade == 1
                            ? "Vasilhame devolvido na troca de 1 produto"
                            : $"Vasilhames devolvidos na troca de {itemInput.Quantidade} produtos",
                        HistoricoEstoque.MotivoVenda,
                        usuario,
                        quantidade: itemInput.Quantidade,
                        vendaId: venda.Id,
                        data: dataHora));
                }
            }

            db.MovimentacoesEstoque.Add(new MovimentacaoEstoque(produto.Id, TipoMovimentacaoEstoque.Saida, itemInput.Quantidade, "Venda"));
        }

        if (command.Desconto > 0)
        {
            venda.AplicarDesconto(command.Desconto);
        }

        venda.DefinirValorEntrega(command.ValorEntrega);

        // Só soma ao saldo devedor quando a venda é fiado; demais formas de pagamento são
        // liquidadas no ato e não alteram o saldo do cliente.
        if (command.FormaPagamento == FormaPagamento.Fiado)
        {
            cliente.AdicionarSaldoDevedor(venda.ValorTotal);
        }
        else
        {
            // Venda paga no ato: registra o pagamento recebido (não abate saldo devedor, pois
            // a venda não foi fiado). O pagamento fica vinculado à venda (VendaId) para permitir
            // estorno/edição quando a venda for excluída.
            db.Pagamentos.Add(new Pagamento(cliente.Id, venda.ValorTotal, command.FormaPagamento, "Venda", vendaId: venda.Id));
        }

        db.Vendas.Add(venda);

        // Histórico de estoque consolidado por produto da venda (soma as quantidades do mesmo
        // produto), evitando uma linha por item. A MovimentacaoEstoque acima mantém 1 linha/item.
        foreach (var grupo in command.Itens.GroupBy(i => i.ProdutoId))
        {
            var quantidade = grupo.Sum(i => i.Quantidade);
            var palavra = quantidade == 1 ? "produto" : "produtos";
            db.HistoricosEstoque.Add(new HistoricoEstoque(
                grupo.Key,
                TipoHistoricoEstoque.Venda,
                $"Venda de {quantidade} {palavra}",
                HistoricoEstoque.MotivoVenda,
                usuario,
                quantidade: quantidade,
                vendaId: venda.Id));
        }

        return new RegistrarVendaResult(venda.Id, cliente.Id, venda.ValorTotal, venda.LucroTotal);
    }

    /// <summary>
    /// Sincroniza o acordo de preço personalizado entre cliente e produto: cria/atualiza quando o
    /// preço praticado difere do padrão; remove quando volta ao padrão. Não lança quando o preço é
    /// inválido — a validação (FluentValidation) já impede preços &lt;= 0.
    /// </summary>
    private static async Task AtualizarAcordoPrecoAsync(
        AppDbContext db, Guid clienteId, Guid produtoId, decimal precoPraticado, decimal precoPadrao, CancellationToken cancellationToken)
    {
        var acordo = await db.PrecosPersonalizadosClientes
            .FirstOrDefaultAsync(p => p.ClienteId == clienteId && p.ProdutoId == produtoId, cancellationToken);

        if (precoPraticado == precoPadrao)
        {
            // Voltou ao preço padrão do produto: o acordo deixa de existir.
            if (acordo is not null)
            {
                db.PrecosPersonalizadosClientes.Remove(acordo);
            }

            return;
        }

        // Preço prático diferente do padrão (acordo negociado).
        if (acordo is null)
        {
            db.PrecosPersonalizadosClientes.Add(new PrecoPersonalizadoCliente(clienteId, produtoId, precoPraticado));
        }
        else
        {
            acordo.AtualizarPreco(precoPraticado);
        }
    }
}