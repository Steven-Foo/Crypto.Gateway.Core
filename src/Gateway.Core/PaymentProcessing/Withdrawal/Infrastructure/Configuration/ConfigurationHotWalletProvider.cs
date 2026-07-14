using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Configuration;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Configuration;

/// <summary>
/// Resolves the per-chain hot wallet from config <c>Withdrawal:HotWallets</c>. The KeyReference points at
/// the wallet's signing key in KeyManagement/KMS — this config holds no key material. A missing entry
/// throws: a withdrawal must never be built without a known source and key reference.
/// </summary>
public sealed class ConfigurationHotWalletProvider : IHotWalletProvider
{
    private readonly IReadOnlyDictionary<Chain, HotWallet> _wallets;

    public ConfigurationHotWalletProvider(IConfiguration configuration)
    {
        var wallets = new Dictionary<Chain, HotWallet>();

        foreach (var child in configuration.GetSection("Withdrawal:HotWallets").GetChildren())
        {
            if (!Enum.TryParse<Chain>(child.Key, ignoreCase: true, out var chain))
                continue;

            var address = child["Address"];
            var keyReference = child["KeyReference"];
            if (!string.IsNullOrWhiteSpace(address) && !string.IsNullOrWhiteSpace(keyReference))
                wallets[chain] = new HotWallet(address, keyReference);
        }

        _wallets = wallets;
    }

    public HotWallet For(Chain chain) =>
        _wallets.TryGetValue(chain, out var wallet)
            ? wallet
            : throw new InvalidOperationException(
                $"No hot wallet configured for {chain}. Add 'Withdrawal:HotWallets:{chain}' with an Address and KeyReference.");
}
