using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Persistence;

public sealed class ScanCursorMap : IEntityTypeConfiguration<ScanCursor>
{
    public void Configure(EntityTypeBuilder<ScanCursor> builder)
    {
        builder.ToTable("ScanCursor");

        // One row per chain; the chain is the natural key. Low-write, so a clustered PK is fine.
        builder.HasKey(c => c.Chain);
        builder.Property(c => c.Chain).HasConversion<string>().HasMaxLength(16).ValueGeneratedNever();
        builder.Property(c => c.LastScannedBlock).IsRequired();
        builder.Property(c => c.UpdatedAt).IsRequired();
    }
}
