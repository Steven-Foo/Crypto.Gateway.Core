using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Persistence;

public sealed class MerchantUserTwoFactorMap : IEntityTypeConfiguration<MerchantUserTwoFactor>
{
    public void Configure(EntityTypeBuilder<MerchantUserTwoFactor> builder)
    {
        builder.ToTable("MerchantUserTwoFactor");

        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();

        builder.Property(f => f.SecretCiphertext).IsUnicode(false).HasMaxLength(512).IsRequired();
        builder.Property(f => f.Status).HasConversion<string>().IsUnicode(false).HasMaxLength(16).IsRequired();

        builder.Ignore(f => f.IsEnrolled);
        builder.Ignore(f => f.DomainEvents);

        // One factor per account, enforced at the database: two concurrent enrollment requests would
        // otherwise both pass a check-then-insert and leave the account with two secrets, only one of which
        // is on the phone.
        builder.HasIndex(f => f.MerchantUserId).IsUnique();

        // Every tenant-scoped read filters on (MerchantId, MerchantUserId).
        builder.HasIndex(f => new { f.MerchantId, f.MerchantUserId });

        builder.Property<byte[]>("RowVersion").IsRowVersion();
    }
}

public sealed class MerchantUserRecoveryCodeMap : IEntityTypeConfiguration<MerchantUserRecoveryCode>
{
    public void Configure(EntityTypeBuilder<MerchantUserRecoveryCode> builder)
    {
        builder.ToTable("MerchantUserRecoveryCode");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.CodeHash).IsUnicode(false).HasMaxLength(256).IsRequired();

        builder.Ignore(c => c.IsUsable);
        builder.Ignore(c => c.DomainEvents);

        builder.HasSeqClusteredIndex();
        builder.HasIndex(c => new { c.MerchantUserId, c.UsedAt });
    }
}

public sealed class MerchantTwoFactorRepository(MerchantIdentityDbContext db) : IMerchantTwoFactorRepository
{
    public Task<MerchantUserTwoFactor?> FindAsync(
        Guid merchantId, Guid merchantUserId, CancellationToken cancellationToken = default) =>
        db.MerchantUserTwoFactors.FirstOrDefaultAsync(
            f => f.MerchantId == merchantId && f.MerchantUserId == merchantUserId, cancellationToken);

    public Task<MerchantUserTwoFactor?> FindByUserAsync(
        Guid merchantUserId, CancellationToken cancellationToken = default) =>
        db.MerchantUserTwoFactors.FirstOrDefaultAsync(f => f.MerchantUserId == merchantUserId, cancellationToken);

    public void Add(MerchantUserTwoFactor factor) => db.MerchantUserTwoFactors.Add(factor);

    public async Task<IReadOnlyList<MerchantUserRecoveryCode>> ListRecoveryCodesAsync(
        Guid merchantUserId, bool unusedOnly, CancellationToken cancellationToken = default)
    {
        var query = db.MerchantUserRecoveryCodes.Where(c => c.MerchantUserId == merchantUserId);

        if (unusedOnly)
            query = query.Where(c => c.UsedAt == null);

        // Tracked: the caller may consume or delete what comes back.
        return await query.OrderBy(c => c.CreatedAt).ToListAsync(cancellationToken);
    }

    public void AddRecoveryCodes(IEnumerable<MerchantUserRecoveryCode> codes) =>
        db.MerchantUserRecoveryCodes.AddRange(codes);

    public void RemoveRecoveryCodes(IEnumerable<MerchantUserRecoveryCode> codes) =>
        db.MerchantUserRecoveryCodes.RemoveRange(codes);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
