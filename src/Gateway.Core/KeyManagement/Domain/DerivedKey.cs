using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;

/// <summary>
/// The authoritative record that address X belongs to index I of HD wallet W.
///
/// This lives in KeyManagement, not Wallet, on purpose: signing resolves which key to use entirely
/// inside custody. No other module's table can influence which private key produces a signature.
/// Only the public address is stored — the private key is derived in memory at signing time and
/// never persisted.
/// </summary>
public sealed class DerivedKey : Entity<Guid>
{
    private DerivedKey(
        Guid id,
        Guid hdWalletId,
        long derivationIndex,
        Chain chain,
        string address,
        string derivationPath,
        DateTimeOffset createdAt) : base(id)
    {
        HdWalletId = hdWalletId;
        DerivationIndex = derivationIndex;
        Chain = chain;
        Address = address;
        DerivationPath = derivationPath;
        CreatedAt = createdAt;
    }

    private DerivedKey() : base(Guid.Empty)
    {
    }

    public Guid HdWalletId { get; private set; }
    public long DerivationIndex { get; private set; }
    public Chain Chain { get; private set; }
    public string Address { get; private set; } = null!;

    /// <summary>The full path (e.g. <c>m/44'/195'/0'/0/17</c>) — recorded so an auditor, or a
    /// disaster-recovery operator holding only the mnemonic, can reproduce this exact key.</summary>
    public string DerivationPath { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    internal static Result<DerivedKey> Create(
        Guid hdWalletId,
        long derivationIndex,
        Chain chain,
        string address,
        string derivationPath,
        DateTimeOffset createdAt)
    {
        if (!Domain.DerivationPath.IsIndexInRange(derivationIndex))
            return Result.Failure<DerivedKey>(KeyManagementErrors.IndexOutOfRange);

        if (string.IsNullOrWhiteSpace(address))
            return Result.Failure<DerivedKey>(Error.Validation("keymgmt.address_required", "Derived address is required."));

        return Result.Success(new DerivedKey(
            Guid.CreateVersion7(), hdWalletId, derivationIndex, chain, address, derivationPath, createdAt));
    }
}
