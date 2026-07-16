using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure.Persistence;

public sealed class EnergyPolicyRepository(EnergyDbContext context) : IEnergyPolicyRepository
{
    public Task<EnergyPolicy?> FindAsync(Chain chain, string walletType, CancellationToken cancellationToken = default) =>
        context.EnergyPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Chain == chain && p.WalletType == walletType && p.IsEnabled, cancellationToken);

    public async Task<IReadOnlyList<EnergyPolicy>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await context.EnergyPolicies.AsNoTracking().ToListAsync(cancellationToken);

    public void Add(EnergyPolicy policy) => context.EnergyPolicies.Add(policy);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
}
