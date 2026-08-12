using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of <see cref="ISecretMaterialStore"/> over <see cref="KeyManagementDbContext"/>.
/// Insert-once/adopt-on-conflict: the unique <c>Reference</c> index is the arbiter, so a fresh seed is
/// persisted exactly once per wallet even when two callers provision concurrently (§7.4 — the DB is the
/// correctness guard, not app logic).
/// </summary>
public sealed class SecretMaterialStore(KeyManagementDbContext context) : ISecretMaterialStore
{
    public async Task<StoredSecretMaterial?> GetAsync(string reference, CancellationToken cancellationToken = default)
    {
        var row = await context.SecretMaterials.AsNoTracking()
            .SingleOrDefaultAsync(m => m.Reference == reference, cancellationToken);

        return row is null ? null : Map(row);
    }

    public async Task<StoredSecretMaterial> GetOrAddAsync(
        StoredSecretMaterial material, CancellationToken cancellationToken = default)
    {
        var existing = await GetAsync(material.Reference, cancellationToken);
        if (existing is not null)
            return existing;

        var row = new SecretMaterial(
            material.Reference, material.Ciphertext, material.Xpub, material.KmsKeyId,
            material.Purpose, material.Chain, DateTimeOffset.UtcNow);

        context.SecretMaterials.Add(row);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return Map(row);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            // The unique Reference index rejected the insert — a concurrent provisioning won the race. Detach
            // our losing row so the context stays reusable, then adopt the winner's material (their seed).
            context.Entry(row).State = EntityState.Detached;

            var winner = await GetAsync(material.Reference, cancellationToken);
            return winner
                ?? throw new InvalidOperationException(
                    $"Secret material '{material.Reference}' failed to insert on a unique-index conflict yet is not present.");
        }
    }

    private static StoredSecretMaterial Map(SecretMaterial row) =>
        new(row.Reference, row.Ciphertext, row.Xpub, row.KmsKeyId, row.Purpose, row.Chain);
}
