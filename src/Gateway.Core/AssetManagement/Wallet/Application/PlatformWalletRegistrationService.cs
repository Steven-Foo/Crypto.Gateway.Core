using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application;

using WalletEntity = Domain.Wallet;

/// <summary>
/// Registers a platform (non-merchant) wallet row for an already-derived/imported key. Idempotent per
/// <c>(chain, address)</c>: a second call for the same address adopts the existing row. The write
/// counterpart to <see cref="IPlatformWalletDirectory"/> — used by Treasury to register the hot wallet.
/// </summary>
public sealed class PlatformWalletRegistrationService(
    IWalletRepository repository,
    TimeProvider timeProvider) : IPlatformWalletRegistrar
{
    public async Task<Result<RegisteredPlatformWallet>> RegisterPlatformWalletAsync(
        Guid derivedKeyId,
        Chain chain,
        string address,
        string walletType,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return Result.Failure<RegisteredPlatformWallet>(WalletErrors.AddressRequired);

        if (!Enum.TryParse<WalletType>(walletType, ignoreCase: true, out var type) || type == WalletType.Deposit)
            return Result.Failure<RegisteredPlatformWallet>(WalletErrors.UnknownWalletType);

        var trimmedAddress = address.Trim();

        // Idempotent adopt: a wallet already at this (chain, address) is the same physical address — return it.
        var existing = await repository.FindByAddressAsync(chain, trimmedAddress, cancellationToken);
        if (existing is not null)
            return Result.Success(ToRegistered(existing));

        var walletResult = WalletEntity.CreatePlatform(derivedKeyId, chain, trimmedAddress, type, description, timeProvider);
        if (walletResult.IsFailure)
            return Result.Failure<RegisteredPlatformWallet>(walletResult.Error!);

        var wallet = walletResult.Value;

        if (await repository.TryAddAsync(wallet, cancellationToken))
            return Result.Success(ToRegistered(wallet));

        // Lost the create race on the unique (Chain, Address) index — adopt the winner.
        var winner = await repository.FindByAddressAsync(chain, trimmedAddress, cancellationToken);
        return winner is not null
            ? Result.Success(ToRegistered(winner))
            : Result.Failure<RegisteredPlatformWallet>(WalletErrors.NotFound);
    }

    private static RegisteredPlatformWallet ToRegistered(WalletEntity wallet) =>
        new(wallet.Id, wallet.Chain, wallet.Address, wallet.WalletType.ToString());
}
