using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;

/// <summary>
/// The authoritative record of which merchant held a wallet, and when. Deposit attribution depends
/// on this history: a payment that lands after an address was released must still be attributable to
/// whoever held it at the time — so assignments are never deleted, only released.
/// </summary>
public sealed class WalletAssignment : Entity<Guid>
{
    private WalletAssignment(Guid id, Guid walletId, Guid merchantId, DateTimeOffset assignedAt) : base(id)
    {
        WalletId = walletId;
        MerchantId = merchantId;
        Status = WalletAssignmentStatus.Active;
        AssignedAt = assignedAt;
    }

    private WalletAssignment() : base(Guid.Empty)
    {
    }

    public Guid WalletId { get; private set; }
    public Guid MerchantId { get; private set; }
    public WalletAssignmentStatus Status { get; private set; }
    public DateTimeOffset AssignedAt { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }

    public bool IsActive => Status == WalletAssignmentStatus.Active;

    internal static WalletAssignment Create(Guid walletId, Guid merchantId, DateTimeOffset assignedAt) =>
        new(Guid.CreateVersion7(), walletId, merchantId, assignedAt);

    internal void Release(DateTimeOffset releasedAt)
    {
        if (Status == WalletAssignmentStatus.Released)
            return;

        Status = WalletAssignmentStatus.Released;
        ReleasedAt = releasedAt;
    }
}
