using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;

public interface ITreasuryColdWalletRegistrar
{
    /// <summary>Registers (or re-points) the cold treasury address for a chain. Idempotent per chain.</summary>
    Task<Result> RegisterAsync(Chain chain, string address, CancellationToken cancellationToken = default);
}

/// <summary>
/// Registers the cold treasury address — a public, watch-only address (no key in the system, §10). Backs both
/// the dev config seed and the staff ops action. Idempotent per chain: re-registering the same chain updates
/// the address rather than creating a second (the unique index on Chain also enforces one per chain).
/// </summary>
public sealed class TreasuryColdWalletRegistrationService(
    ITreasuryColdWalletRepository repository, TimeProvider timeProvider) : ITreasuryColdWalletRegistrar
{
    public async Task<Result> RegisterAsync(Chain chain, string address, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return Result.Failure(TreasuryReloadErrors.AddressRequired);

        var now = timeProvider.GetUtcNow();
        var existing = await repository.FindByChainAsync(chain, cancellationToken);
        if (existing is not null)
        {
            existing.UpdateAddress(address, now);
            await repository.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        var created = TreasuryColdWallet.Register(chain, address, now);
        if (created.IsFailure)
            return Result.Failure(created.Error!);

        await repository.AddAsync(created.Value, cancellationToken);
        return Result.Success();
    }
}
