using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RevendaApp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OrigemMovimentacaoEstoque : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Origem",
                table: "MovimentacoesEstoque",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Manual");

            // Backfill das linhas anteriores à coluna. As regras são determinísticas por construção
            // do código — só existem quatro caminhos que criam movimentação:
            //  1. Estorno de venda (ReversaoVendaHelper) → Entrada com este motivo exato;
            //  2. Venda (RegistrarVendaCommandHandler)    → Saida com motivo "Venda";
            //  3. Compra/movimentação manual             → Entrada;
            //  4. Seed de demonstração                    → só fora de Produção.
            // Logo: toda Entrada que não é estorno é compra, e toda Saida de venda é venda. Nenhuma
            // linha fica ambígua. A ordem importa: o estorno é marcado antes do "resto é compra".
            migrationBuilder.Sql("""
                UPDATE "MovimentacoesEstoque"
                SET "Origem" = 'EstornoVenda'
                WHERE "Tipo" = 'Entrada' AND "Motivo" = 'Estorno de venda excluída';
                """);

            migrationBuilder.Sql("""
                UPDATE "MovimentacoesEstoque"
                SET "Origem" = 'Venda'
                WHERE "Tipo" = 'Saida' AND "Motivo" = 'Venda';
                """);

            // O `AND "Origem" = 'Manual'` é obrigatório: o estorno TAMBÉM é Tipo='Entrada', então sem
            // este filtro o UPDATE acima seria desfeito e os 21 estornos de produção voltariam a
            // contar como compra. A coluna acabou de ser criada com default 'Manual', então este
            // predicado atinge exatamente as linhas ainda não classificadas.
            migrationBuilder.Sql("""
                UPDATE "MovimentacoesEstoque"
                SET "Origem" = 'Compra'
                WHERE "Tipo" = 'Entrada' AND "Origem" = 'Manual';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Origem",
                table: "MovimentacoesEstoque");
        }
    }
}