using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Treasury;

/// <summary>
/// Satisfies the Withdrawal module's <see cref="IHotWalletProvider"/> port from the Treasury module's read
/// Contract (§4.5) — replacing the old raw-config lookup. Throws when Treasury has no hot wallet registered
/// for the chain, preserving the port's contract that a withdrawal is never built without a known source.
/// </summary>
public sealed class TreasuryHotWalletProvider(ITreasuryHotWalletDirectory treasury) : IHotWalletProvider
{
    public async Task<HotWallet> ForAsync(Chain chain, CancellationToken cancellationToken = default)
    {
        var result = await treasury.GetHotWalletAsync(chain, cancellationToken);
        if (result.IsFailure)
            throw new InvalidOperationException(
                $"No Treasury-registered hot wallet for {chain}: {result.Error!.Message}");

        return new HotWallet(result.Value.Address, result.Value.KeyReference);
    }
}
