using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using RevendaApp.Domain.Entities;

namespace RevendaApp.Infrastructure.Configurations;

public class PagamentoConfiguration : IEntityTypeConfiguration<Pagamento>
{
    public void Configure(EntityTypeBuilder<Pagamento> builder)
    {
        builder.ToTable("Pagamentos");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.EmpresaId).IsRequired();

        // Índice de tenant + histórico de pagamentos/dashboard: filtra por empresa e ordena por
        // Data. Substitui o índice simples de EmpresaId — o composto cobre o prefixo.
        builder.HasIndex(p => new { p.EmpresaId, p.Data });

        builder.Property(p => p.Valor).HasPrecision(18, 2);
        builder.Property(p => p.Observacao).HasMaxLength(500);
        builder.Property(p => p.FormaPagamento).HasConversion<string>().HasMaxLength(30);

        builder.HasOne(p => p.Cliente)
            .WithMany()
            .HasForeignKey(p => p.ClienteId)
            .OnDelete(DeleteBehavior.Cascade);

        // Vínculo opcional com a venda não-fiado que originou o pagamento no ato. FK para a venda:
        // se a venda for excluída (soft delete), o pagamento fica vinculado apenas para auditoria
        // e é tratado pelo handler de exclusão/estorno (não cascade, para não apagar pagamentos).
        builder.Property(p => p.VendaId).IsRequired(false);
        builder.HasOne<Venda>()
            .WithMany()
            .HasForeignKey(p => p.VendaId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}