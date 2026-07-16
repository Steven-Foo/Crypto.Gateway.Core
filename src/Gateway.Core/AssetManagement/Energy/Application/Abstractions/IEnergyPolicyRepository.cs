using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;

public interface IEnergyPolicyRepository
{
    /// <summary>The active policy for a wallet type on a chain, or null when none is configured.</summary>
    Task<EnergyPolicy?> FindAsync(Chain chain, string walletType, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EnergyPolicy>> GetAllAsync(CancellationToken cancellationToken = default);

    void Add(EnergyPolicy policy);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
