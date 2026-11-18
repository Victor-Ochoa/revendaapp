using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using RevendaApp.Domain.Entities;

namespace RevendaApp.Infrastructure.Configurations;

public class HistoricoEstoqueConfiguration : IEntityTypeConfiguration<HistoricoEstoque>
{
    public void Configure(EntityTypeBuilder<HistoricoEstoque> builder)
    {
        builder.ToTable("HistoricosEstoque");

        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).ValueGeneratedNever();

        builder.Property(h => h.EmpresaId).IsRequired();
        // Índice composto para os relatórios mensais: filtra por empresa e período. Substitui o
        // índice simples de EmpresaId — o composto cobre o prefixo.
        builder.HasIndex(h => new { h.EmpresaId, h.Data });

        builder.Property(h => h.Tipo).HasConversion<string>().HasMaxLength(30);
        builder.Property(h => h.Descricao).HasMaxLength(500);
        builder.Property(h => h.Motivo).HasMaxLength(300);
        builder.Property(h => h.Usuario).IsRequired().HasMaxLength(200);

        // VendedorId/VendaId são colunas apenas de registro, sem FK.
        builder.HasOne(h => h.Produto)
            .WithMany()
            .HasForeignKey(h => h.ProdutoId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}