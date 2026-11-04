using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using RevendaApp.Domain.Entities;

namespace RevendaApp.Infrastructure.Configurations;

public class VendaConfiguration : IEntityTypeConfiguration<Venda>
{
    public void Configure(EntityTypeBuilder<Venda> builder)
    {
        builder.ToTable("Vendas");

        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();

        builder.Property(v => v.EmpresaId).IsRequired();

        // Índice de tenant + dashboard: filtra por empresa e período, ordenando por DataHora.
        // Substitui o índice simples de EmpresaId — o composto cobre o prefixo.
        builder.HasIndex(v => new { v.EmpresaId, v.DataHora });

        builder.Property(v => v.FormaPagamento).HasConversion<string>().HasMaxLength(30);
        builder.Property(v => v.Desconto).HasPrecision(18, 2);
        builder.Property(v => v.ValorEntrega).HasPrecision(18, 2);
        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(30);

        // VendedorId é apenas registro (guid do usuário autenticado que registrou a venda), sem FK.
        builder.Property(v => v.VendedorId).IsRequired(false);

        // Exclusão lógica: venda excluída permanece no banco (para não quebrar referências), mas
        // deixa de contar nas queries e no dashboard.
        builder.Property(v => v.ExcluidaEm).IsRequired(false);

        builder.Ignore(v => v.ValorBruto);
        builder.Ignore(v => v.ValorTotal);
        builder.Ignore(v => v.LucroTotal);

        builder.HasOne(v => v.Cliente)
            .WithMany()
            .HasForeignKey(v => v.ClienteId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(v => v.Itens)
            .WithOne()
            .HasForeignKey(i => i.VendaId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(v => v.Itens).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}