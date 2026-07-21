using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Persistence;

public sealed class StaffSessionMap : IEntityTypeConfiguration<StaffSession>
{
    public void Configure(EntityTypeBuilder<StaffSession> builder)
    {
        builder.ToTable("StaffSession");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.TokenHash).IsUnicode(false).HasMaxLength(128).IsRequired();
        builder.Property(s => s.Role).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.Ignore(s => s.DomainEvents);

        // Append-heavy: one row per login, non-clustered GUID PK + monotonic clustered Seq.
        builder.HasSeqClusteredIndex();

        // The validator's hot query: look up a presented token's hash.
        builder.HasIndex(s => s.TokenHash).IsUnique();

        builder.HasIndex(s => s.StaffUserId);
    }
}
