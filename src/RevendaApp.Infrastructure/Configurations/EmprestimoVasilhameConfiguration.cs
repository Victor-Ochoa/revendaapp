using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using RevendaApp.Domain.Entities;

namespace RevendaApp.Infrastructure.Configurations;

public class EmprestimoVasilhameConfiguration : IEntityTypeConfiguration<EmprestimoVasilhame>
{
    public void Configure(EntityTypeBuilder<EmprestimoVasilhame> builder)
    {
        builder.ToTable("EmprestimosVasilhames");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.EmpresaId).IsRequired();
        // Índice composto para os relatórios mensais: filtra por empresa e devolução (em aberto vs.
        // devolvido). Substitui o índice simples de EmpresaId — o composto cobre o prefixo.
        builder.HasIndex(e => new { e.EmpresaId, e.DevolvidoEm });

        builder.Property(e => e.Quantidade).IsRequired();
        builder.Property(e => e.Data).IsRequired();
        builder.Property(e => e.Observacao).IsRequired(false);
        builder.Property(e => e.QuantidadeDevolvida).HasDefaultValue(0);
        builder.Property(e => e.DevolvidoEm).IsRequired(false);

        builder.Ignore(e => e.Saldo);

        builder.HasOne(e => e.Cliente)
            .WithMany()
            .HasForeignKey(e => e.ClienteId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Produto)
            .WithMany()
            .HasForeignKey(e => e.ProdutoId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Venda)
            .WithMany()
            .HasForeignKey(e => e.VendaId)
            .OnDelete(DeleteBehavior.SetNull);

        // Consulta comum: quais vasilhames um cliente ainda deve, por produto.
        builder.HasIndex(e => new { e.ClienteId, e.ProdutoId });
    }
}