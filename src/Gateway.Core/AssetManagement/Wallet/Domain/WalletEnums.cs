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
    Disabled = 2,
}

public enum WalletAssignmentStatus
{
    Active = 1,
    Released = 2,
}
