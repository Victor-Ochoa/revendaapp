using Microsoft.EntityFrameworkCore.Migrations;

// CA1861: o gerador do EF Core emite "new[]{...}" inline para os índices compostos; suprimir no
// arquivo gerado (o código de migração não é editado manualmente).
#pragma warning disable CA1861

#nullable disable

namespace RevendaApp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class IndicesDashboard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Vendas_EmpresaId",
                table: "Vendas");

            migrationBuilder.DropIndex(
                name: "IX_Pagamentos_EmpresaId",
                table: "Pagamentos");

            migrationBuilder.DropIndex(
                name: "IX_Clientes_EmpresaId",
                table: "Clientes");

            migrationBuilder.CreateIndex(
                name: "IX_Vendas_EmpresaId_DataHora",
                table: "Vendas",
                columns: new[] { "EmpresaId", "DataHora" });

            migrationBuilder.CreateIndex(
                name: "IX_Pagamentos_EmpresaId_Data",
                table: "Pagamentos",
                columns: new[] { "EmpresaId", "Data" });

            migrationBuilder.CreateIndex(
                name: "IX_Clientes_EmpresaId_SaldoDevedor",
                table: "Clientes",
                columns: new[] { "EmpresaId", "SaldoDevedor" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Vendas_EmpresaId_DataHora",
                table: "Vendas");

            migrationBuilder.DropIndex(
                name: "IX_Pagamentos_EmpresaId_Data",
                table: "Pagamentos");

            migrationBuilder.DropIndex(
                name: "IX_Clientes_EmpresaId_SaldoDevedor",
                table: "Clientes");

            migrationBuilder.CreateIndex(
                name: "IX_Vendas_EmpresaId",
                table: "Vendas",
                column: "EmpresaId");

            migrationBuilder.CreateIndex(
                name: "IX_Pagamentos_EmpresaId",
                table: "Pagamentos",
                column: "EmpresaId");

            migrationBuilder.CreateIndex(
                name: "IX_Clientes_EmpresaId",
                table: "Clientes",
                column: "EmpresaId");
        }
    }
}