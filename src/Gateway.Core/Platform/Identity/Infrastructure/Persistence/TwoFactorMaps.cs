using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Persistence;

public sealed class StaffTwoFactorMap : IEntityTypeConfiguration<StaffTwoFactor>
{
    public void Configure(EntityTypeBuilder<StaffTwoFactor> builder)
    {
        builder.ToTable("StaffTwoFactor");

        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();

        // The AES-GCM blob is base64 ASCII, not text a human reads — varchar, not nvarchar (§7.2).
        builder.Property(f => f.SecretCiphertext).IsUnicode(false).HasMaxLength(512).IsRequired();

        // Stored as its name, so a new status is not a migration (the convention every module here follows).
        builder.Property(f => f.Status).HasConversion<string>().IsUnicode(false).HasMaxLength(16).IsRequired();

        builder.Ignore(f => f.IsEnrolled);
        builder.Ignore(f => f.DomainEvents);

        // One factor per account, enforced at the database rather than by a check-then-insert: two concurrent
        // enrollment requests would otherwise both pass the check and leave the account with two secrets, only
        // one of which is on the phone.
        builder.HasIndex(f => f.StaffUserId).IsUnique();

        builder.Property<byte[]>("RowVersion").IsRowVersion();
    }
}

public sealed class StaffRecoveryCodeMap : IEntityTypeConfiguration<StaffRecoveryCode>
{
    public void Configure(EntityTypeBuilder<StaffRecoveryCode> builder)
    {
        builder.ToTable("StaffRecoveryCode");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        // PBKDF2 "{iterations}.{salt}.{hash}" — ASCII.
        builder.Property(c => c.CodeHash).IsUnicode(false).HasMaxLength(256).IsRequired();

        builder.Ignore(c => c.IsUsable);
        builder.Ignore(c => c.DomainEvents);

        // Append-heavy: a batch per enrollment, never updated except to stamp UsedAt.
        builder.HasSeqClusteredIndex();

        // The login path's query: this account's unused codes.
        builder.HasIndex(c => new { c.StaffUserId, c.UsedAt });
    }
}

public sealed class TwoFactorPolicyVersionMap : IEntityTypeConfiguration<TwoFactorPolicyVersion>
{
    public void Configure(EntityTypeBuilder<TwoFactorPolicyVersion> builder)
    {
        builder.ToTable("TwoFactorPolicyVersion");

        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();

        // Action codes are host-owned ASCII identifiers; the list is small but unbounded in principle.
        builder.Property(v => v.GuardedActionsCsv).IsUnicode(false).HasMaxLength(4000).IsRequired();
        builder.Property(v => v.Note).HasMaxLength(512);
        builder.Property(v => v.UpdatedBy).HasMaxLength(128).IsRequired();

        builder.Ignore(v => v.DomainEvents);

        // Append-only history: the clustered Seq IS the ordering that resolves "the version in force", which
        // a timestamp alone could not do if two saves landed in the same instant.
        builder.HasSeqClusteredIndex();

        builder.HasIndex(v => v.UpdatedAt);
    }
}
