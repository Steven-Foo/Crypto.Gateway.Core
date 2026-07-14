using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;

public sealed class AccountBalanceMap : IEntityTypeConfiguration<AccountBalance>
{
    public void Configure(EntityTypeBuilder<AccountBalance> builder)
    {
        builder.ToTable("AccountBalance");

        // Id IS the AccountId — one balance row per account, shared PK with Account.
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();

        // BigInteger -> decimal(38,0). Non-negative by invariant; the CHECK is defence in depth.
        builder.Property(b => b.Balance).IsRequired();
        builder.Property(b => b.LastEntryId);

        // Optimistic concurrency: two posters to one account can't both win a stale-read update.
        builder.Property<byte[]>("RowVersion").IsRowVersion();

        builder.Ignore(b => b.DomainEvents);

        builder.HasOne<Account>()
            .WithOne()
            .HasForeignKey<AccountBalance>(b => b.Id)
            .OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(t => t.HasCheckConstraint("CK_AccountBalance_NonNegative", "[Balance] >= 0"));
    }
}
