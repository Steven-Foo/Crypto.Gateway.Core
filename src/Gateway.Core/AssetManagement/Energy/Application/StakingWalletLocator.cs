using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;

/// <summary>The platform staking (energy) wallet for a chain — the source of freeze/delegate operations.</summary>
public sealed record StakingWallet(Guid WalletId, string Address);

/// <summary>
/// Locates the single platform staking wallet (<c>WalletType.Energy</c>) for a chain via the Wallet module's
/// Contract (§4.5). Per the KMS topology, staking has its own dedicated wallet + key; this finds it by type.
/// Returns null when none — or more than one — is registered, so callers stay inert rather than guessing.
/// </summary>
public sealed class StakingWalletLocator(IPlatformWalletDirectory wallets)
{
    /// <summary>Matches <c>WalletType.Energy.ToString()</c>, the string form wallet type crosses the boundary as.</summary>
    public const string EnergyWalletType = "Energy";

    public async Task<StakingWallet?> FindAsync(Chain chain, CancellationToken cancellationToken = default)
    {
        var platform = await wallets.GetPlatformWalletsAsync(chain, cancellationToken);
        var energy = platform.Where(w => w.WalletType == EnergyWalletType).ToList();

        // 0 → not registered; >1 → ambiguous. Either way, refuse to pick one silently.
        return energy.Count == 1 ? new StakingWallet(energy[0].WalletId, energy[0].Address) : null;
    }
}
