using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Persistence;

public sealed class MerchantTwoFactorPolicyVersionMap : IEntityTypeConfiguration<MerchantTwoFactorPolicyVersion>
{
    public void Configure(EntityTypeBuilder<MerchantTwoFactorPolicyVersion> builder)
    {
        builder.ToTable("MerchantTwoFactorPolicyVersion");

        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();

        builder.Property(v => v.GuardedActionsCsv).IsUnicode(false).HasMaxLength(4000).IsRequired();
        builder.Property(v => v.Note).HasMaxLength(512);
        builder.Property(v => v.UpdatedBy).HasMaxLength(128).IsRequired();

        builder.Ignore(v => v.IsPlatformMinimum);
        builder.Ignore(v => v.DomainEvents);

        // Append-only: the clustered Seq IS insertion order, which resolves "the version in force" — not
        // UpdatedAt (two saves can tie inside a clock tick) and not Id (SQL Server sorts a GUID by its last
        // bytes, unrelated to write order). Same rule as identity.TwoFactorPolicyVersion.
        builder.HasSeqClusteredIndex();

        // Every read is "the latest row for this layer": (MerchantId) with Seq, NULL = the platform minimum.
        builder.HasIndex(v => v.MerchantId);
    }
}

public sealed class MerchantTwoFactorPolicyRepository(MerchantIdentityDbContext db) : IMerchantTwoFactorPolicyRepository
{
    public Task<MerchantTwoFactorPolicyVersion?> FindLatestPlatformAsync(CancellationToken cancellationToken = default) =>
        db.MerchantTwoFactorPolicyVersions
            .AsNoTracking()
            .Where(v => v.MerchantId == null)
            .OrderByDescending(v => EF.Property<long>(v, "Seq"))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<MerchantTwoFactorPolicyVersion?> FindLatestForMerchantAsync(
        Guid merchantId, CancellationToken cancellationToken = default) =>
        db.MerchantTwoFactorPolicyVersions
            .AsNoTracking()
            .Where(v => v.MerchantId == merchantId)
            .OrderByDescending(v => EF.Property<long>(v, "Seq"))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<MerchantTwoFactorPolicyVersion>> ListHistoryAsync(
        Guid? merchantId, int limit, CancellationToken cancellationToken = default) =>
        await db.MerchantTwoFactorPolicyVersions
            .AsNoTracking()
            .Where(v => v.MerchantId == merchantId)
            .OrderByDescending(v => EF.Property<long>(v, "Seq"))
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);

    public void Add(MerchantTwoFactorPolicyVersion version) => db.MerchantTwoFactorPolicyVersions.Add(version);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
