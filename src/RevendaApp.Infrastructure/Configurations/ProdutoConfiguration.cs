using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using RevendaApp.Domain.Entities;

namespace RevendaApp.Infrastructure.Configurations;

public class ProdutoConfiguration : IEntityTypeConfiguration<Produto>
{
    public void Configure(EntityTypeBuilder<Produto> builder)
    {
        builder.ToTable("Produtos");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.EmpresaId).IsRequired();
        builder.HasIndex(p => p.EmpresaId);

        builder.Property(p => p.Nome).IsRequired().HasMaxLength(200);
        builder.Property(p => p.Tipo).HasConversion<string>().HasMaxLength(50);
        builder.Property(p => p.PrecoVenda).HasPrecision(18, 2);
        builder.Property(p => p.PrecoCusto).HasPrecision(18, 2);
        builder.Property(p => p.PrecoGasDoPovo).HasPrecision(18, 2);
        builder.Property(p => p.Ativo).HasDefaultValue(true);

        builder.Ignore(p => p.EstoqueBaixo);

        // Concorrência otimista: o estoque é lido, validado em memória e gravado. Sem o token, duas
        // vendas simultâneas do último item passam as duas e o estoque fica negativo. `xmin` é a coluna
        // de sistema do Postgres com o id da última transação que alterou a linha.
        builder.Property<uint>("xmin").IsRowVersion();
    }
}