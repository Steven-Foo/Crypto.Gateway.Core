using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence;

public sealed class SecretMaterialMap : IEntityTypeConfiguration<SecretMaterial>
{
    public void Configure(EntityTypeBuilder<SecretMaterial> builder)
    {
        builder.ToTable("SecretMaterial");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.Reference).IsUnicode(false).HasMaxLength(512).IsRequired();

        // The KMS-sealed seed. varbinary(max) — a KMS ciphertext blob is a few hundred bytes, but the column
        // is deliberately generous. There is no plaintext-seed column, and KeyManagementSecurityTests asserts
        // no seed/private-key column is ever added (§10).
        builder.Property(m => m.Ciphertext).IsRequired();

        // Public account xpub — public material, stored in the clear for watch-only derivation (§8).
        builder.Property(m => m.Xpub).IsUnicode(false).HasMaxLength(256).IsRequired();

        builder.Property(m => m.KmsKeyId).IsUnicode(false).HasMaxLength(512).IsRequired();
        builder.Property(m => m.Purpose).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(m => m.Chain).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(m => m.CreatedAt).IsRequired();

        // The exactly-once seed arbiter: two concurrent first-provisionings for the same wallet both target
        // this reference; the unique index lets only one insert win, and the loser adopts it.
        builder.HasIndex(m => m.Reference).IsUnique();
    }
}
