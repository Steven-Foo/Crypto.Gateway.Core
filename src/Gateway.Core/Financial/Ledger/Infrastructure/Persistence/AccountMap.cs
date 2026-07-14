using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;

public sealed class AccountMap : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("Account");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.AccountType).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(a => a.OwnerType).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.OwnerId); // null for treasury/system accounts
        builder.Property(a => a.AssetId).IsRequired();
        builder.Property(a => a.NormalSide).HasConversion<string>().HasMaxLength(8).IsRequired();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.Property<byte[]>("RowVersion").IsRowVersion();

        builder.Ignore(a => a.IsActive);
        builder.Ignore(a => a.DomainEvents);

        // The natural key. In SQL Server a composite UNIQUE treats NULLs as equal, so this also caps
        // treasury/system accounts (OwnerId NULL) at one per (asset, type) — the get-or-create arbiter.
        // HasFilter(null) overrides EF's default "[OwnerId] IS NOT NULL" filter (added because OwnerId is
        // nullable); with that filter the NULL-owner treasury rows would escape the uniqueness guarantee.
        builder.HasIndex(a => new { a.OwnerType, a.OwnerId, a.AssetId, a.AccountType })
            .IsUnique()
            .HasFilter(null)
            .HasDatabaseName("UX_Account_Natural");

        builder.HasIndex(a => new { a.OwnerId, a.AssetId }).HasFilter("[OwnerId] IS NOT NULL");
    }
}
