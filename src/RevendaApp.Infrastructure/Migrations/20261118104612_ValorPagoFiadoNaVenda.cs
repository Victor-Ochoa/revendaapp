using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RevendaApp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ValorPagoFiadoNaVenda : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ValorPagoFiado",
                table: "Vendas",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // Vendas fiado quitadas antes desta coluna foram pagas integralmente: o valor é exato.
            // ValorTotal não é coluna — soma os itens (PrecoUnitario × Quantidade), desconta o
            // desconto da venda e soma a entrega, na mesma fórmula de Venda.ValorTotal.
            migrationBuilder.Sql("""
                UPDATE "Vendas" v
                SET "ValorPagoFiado" = itens."ValorBruto" - v."Desconto" + v."ValorEntrega"
                FROM (
                    SELECT "VendaId", SUM("PrecoUnitario" * "Quantidade") AS "ValorBruto"
                    FROM "ItensVenda"
                    GROUP BY "VendaId"
                ) itens
                WHERE itens."VendaId" = v."Id"
                  AND v."FormaPagamento" = 'Fiado'
                  AND v."Status" = 'FiadoQuitado';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ValorPagoFiado",
                table: "Vendas");
        }
    }
}