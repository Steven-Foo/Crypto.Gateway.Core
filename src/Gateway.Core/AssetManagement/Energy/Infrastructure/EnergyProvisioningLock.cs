using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;
using CryptoPaymentEngine.Infrastructure.Locking;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure;

/// <summary>
/// The per-chain Redis lock around staking-wallet creation. Fails closed: if the lock cannot be taken (Redis
/// down, or another request holding it past the wait), the request errors rather than racing to create a
/// second wallet.
/// </summary>
public sealed class EnergyProvisioningLock(IDistributedLockFactory locks) : IEnergyProvisioningLock
{
    public Task<IAsyncDisposable> AcquireAsync(Chain chain, CancellationToken cancellationToken = default) =>
        locks.AcquireAsync($"energy:staking-wallet:provision:{chain}", TimeSpan.FromSeconds(30), cancellationToken);
}
