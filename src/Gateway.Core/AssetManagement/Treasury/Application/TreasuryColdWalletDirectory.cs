using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;

/// <summary>Resolves the registered cold treasury address for a chain from Treasury's own persistence.</summary>
public sealed class TreasuryColdWalletDirectory(ITreasuryColdWalletRepository repository) : ITreasuryColdWalletDirectory
{
    public async Task<Result<ColdTreasuryWallet>> GetAsync(Chain chain, CancellationToken cancellationToken = default)
    {
        var wallet = await repository.FindByChainAsync(chain, cancellationToken);
        return wallet is null
            ? Result.Failure<ColdTreasuryWallet>(TreasuryReloadErrors.ColdWalletNotConfigured)
            : Result.Success(new ColdTreasuryWallet(chain, wallet.Address));
    }
}
