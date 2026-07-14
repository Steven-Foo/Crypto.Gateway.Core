using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;

public sealed class JournalEntryMap : IEntityTypeConfiguration<JournalEntry>
{
    public void Configure(EntityTypeBuilder<JournalEntry> builder)
    {
        builder.ToTable("JournalEntry");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.JournalId).IsRequired();
        builder.Property(e => e.AccountId).IsRequired();
        builder.Property(e => e.AssetId).IsRequired();

        // BigInteger -> decimal(38,0) via BigIntegerTypeMapping (UseBigIntegerMoney). Unsigned base units.
        builder.Property(e => e.Debit).IsRequired();
        builder.Property(e => e.Credit).IsRequired();

        builder.Ignore(e => e.IsDebit);
        builder.Ignore(e => e.DomainEvents);

        builder.HasSeqClusteredIndex();

        builder.HasIndex(e => e.AccountId);

        // The DB guarantees a line is a debit XOR a credit, whatever code path writes it.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_JournalEntry_DebitXorCredit",
            "([Debit] = 0 AND [Credit] > 0) OR ([Debit] > 0 AND [Credit] = 0)"));
    }
}
