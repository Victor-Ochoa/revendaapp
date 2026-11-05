using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using RevendaApp.Domain.Entities;

namespace RevendaApp.Infrastructure.Configurations;

public class ClienteConfiguration : IEntityTypeConfiguration<Cliente>
{
    public void Configure(EntityTypeBuilder<Cliente> builder)
    {
        builder.ToTable("Clientes");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.EmpresaId).IsRequired();

        builder.Property(c => c.Nome).IsRequired().HasMaxLength(200);
        builder.Property(c => c.Telefone).IsRequired().HasMaxLength(20);
        builder.Property(c => c.Endereco).IsRequired().HasMaxLength(300);
        builder.Property(c => c.SaldoDevedor).HasPrecision(18, 2);

        // O telefone é a chave natural usada para localizar o cliente na tela de venda — único
        // apenas dentro da empresa (duas empresas podem ter o mesmo telefone cadastrado).
        builder.HasIndex(c => new { c.EmpresaId, c.Telefone }).IsUnique();

        // Índice de tenant + "top devedores" do dashboard: filtra por empresa e ordena desc por
        // SaldoDevedor. Substitui o índice simples de EmpresaId — o composto cobre o prefixo.
        // Convive com o único (EmpresaId, Telefone), que serve outra consulta.
        builder.HasIndex(c => new { c.EmpresaId, c.SaldoDevedor });

        // Mesma razão do Produto: SaldoDevedor é read-modify-write (venda fiado + pagamento).
        builder.Property<uint>("xmin").IsRowVersion();
    }
}