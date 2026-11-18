using Microsoft.EntityFrameworkCore.Migrations;

// CA1861: o gerador do EF Core emite "new[]{...}" inline para os índices compostos; suprimir no
// arquivo gerado (o código de migração não é editado manualmente).
#pragma warning disable CA1861

#nullable disable

namespace RevendaApp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class IndicesRelatorios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MovimentacoesEstoque_EmpresaId",
                table: "MovimentacoesEstoque");

            migrationBuilder.DropIndex(
                name: "IX_HistoricosEstoque_EmpresaId",
                table: "HistoricosEstoque");

            migrationBuilder.DropIndex(
                name: "IX_EmprestimosVasilhames_EmpresaId",
                table: "EmprestimosVasilhames");

            migrationBuilder.CreateIndex(
                name: "IX_MovimentacoesEstoque_EmpresaId_Data",
                table: "MovimentacoesEstoque",
                columns: new[] { "EmpresaId", "Data" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricosEstoque_EmpresaId_Data",
                table: "HistoricosEstoque",
                columns: new[] { "EmpresaId", "Data" });

            migrationBuilder.CreateIndex(
                name: "IX_EmprestimosVasilhames_EmpresaId_DevolvidoEm",
                table: "EmprestimosVasilhames",
                columns: new[] { "EmpresaId", "DevolvidoEm" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MovimentacoesEstoque_EmpresaId_Data",
                table: "MovimentacoesEstoque");

            migrationBuilder.DropIndex(
                name: "IX_HistoricosEstoque_EmpresaId_Data",
                table: "HistoricosEstoque");

            migrationBuilder.DropIndex(
                name: "IX_EmprestimosVasilhames_EmpresaId_DevolvidoEm",
                table: "EmprestimosVasilhames");

            migrationBuilder.CreateIndex(
                name: "IX_MovimentacoesEstoque_EmpresaId",
                table: "MovimentacoesEstoque",
                column: "EmpresaId");

            migrationBuilder.CreateIndex(
                name: "IX_HistoricosEstoque_EmpresaId",
                table: "HistoricosEstoque",
                column: "EmpresaId");

            migrationBuilder.CreateIndex(
                name: "IX_EmprestimosVasilhames_EmpresaId",
                table: "EmprestimosVasilhames",
                column: "EmpresaId");
        }
    }
}