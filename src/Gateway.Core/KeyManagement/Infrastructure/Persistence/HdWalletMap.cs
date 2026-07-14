using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence;

public sealed class HdWalletMap : IEntityTypeConfiguration<HdWallet>
{
    public void Configure(EntityTypeBuilder<HdWallet> builder)
    {
        builder.ToTable("HdWallet");

        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).ValueGeneratedNever();

        builder.Property(w => w.Name).HasMaxLength(128).IsRequired();
        builder.Property(w => w.Chain).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(w => w.Purpose).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(w => w.Scheme).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(w => w.SecretProvider).HasConversion<string>().HasMaxLength(32).IsRequired();

        // References to key material, never key material. There is no seed/mnemonic/private-key column,
        // and KeyManagementSecurityTests asserts none is ever added.
        builder.Property(w => w.SecretReference).IsUnicode(false).HasMaxLength(512).IsRequired();
        builder.Property(w => w.PublicKeyReference).IsUnicode(false).HasMaxLength(512);

        builder.Property(w => w.DerivationPathValue)
            .HasColumnName("DerivationPath")
            .IsUnicode(false)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(w => w.NextDerivationIndex).IsRequired();
        builder.Property(w => w.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(w => w.Description).HasMaxLength(256);
        builder.Property<byte[]>("RowVersion").IsRowVersion();

        builder.Ignore(w => w.DerivationPath);
        builder.Ignore(w => w.IsActive);
        builder.Ignore(w => w.SupportsWatchOnlyDerivation);
        builder.Ignore(w => w.IsExhausted);
        builder.Ignore(w => w.DomainEvents);

        // Exactly one active HD wallet per (chain, purpose): otherwise "allocate the next deposit
        // address for TRON" is nondeterministic and addresses scatter across pools.
        builder.HasIndex(w => new { w.Chain, w.Purpose })
            .IsUnique()
            .HasFilter("[Status] = 'Active'");

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_HdWallet_DerivationIndex_Range",
            $"[NextDerivationIndex] >= 0 AND [NextDerivationIndex] <= {DerivationPath.MaxIndex + 1}"));

        // A watch-only (secp256k1) wallet must carry an xpub reference; an ed25519 wallet must not.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_HdWallet_PublicKeyReference_MatchesScheme",
            "([Scheme] = 'Bip32Secp256k1' AND [PublicKeyReference] IS NOT NULL) OR " +
            "([Scheme] = 'Slip10Ed25519' AND [PublicKeyReference] IS NULL)"));
    }
}
