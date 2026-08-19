namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;

public enum WalletType
{
    /// <summary>A merchant-facing receive address. HD-derived and assigned to exactly one merchant.</summary>
    Deposit = 1,

    /// <summary>Platform hot wallet for outbound payments. Not merchant-assigned.</summary>
    HotWithdrawal = 2,

    /// <summary>Platform treasury. Not merchant-assigned.</summary>
    Treasury = 3,

    /// <summary>Offline / cold storage. Not merchant-assigned.</summary>
    Cold = 4,

    /// <summary>TRON energy/bandwidth staking wallet. Not merchant-assigned.</summary>
    Energy = 5,
}

public enum WalletStatus
{
    Active = 1,

    /// <summary>Permanent decommission — clears the merchant assignment (see <c>Wallet.Disable</c>). Not
    /// reversible; use <see cref="Suspended"/> for a temporary hold instead.</summary>
    Disabled = 2,

    /// <summary>A temporary, staff-initiated hold (see <c>Wallet.Suspend</c>/<c>Wallet.Resume</c>) — e.g. an
    /// address that received an unexpected/off-flow transfer and is being held for investigation. Unlike
    /// <see cref="Disabled"/>, the merchant assignment is left untouched so <c>Resume</c> restores the exact
    /// same address to the exact same merchant.</summary>
    Suspended = 3,
}

public enum WalletAssignmentStatus
{
    Active = 1,
    Released = 2,
}
