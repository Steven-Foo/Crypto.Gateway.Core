namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;

/// <summary>
/// The lifecycle of a treasury→hot reload. The cold treasury key is NOT in the system, so a human signs the
/// built transaction client-side (the raw key never reaches the backend, §10); the backend only ever holds the
/// unsigned and signed blobs. Moves funds between two platform-controlled addresses, so it never touches the
/// ledger (§14) — total custody is unchanged, only its location.
/// </summary>
public enum TreasuryReloadStatus
{
    /// <summary>Built (unsigned) and waiting for the operator to sign it client-side and submit the signed blob.</summary>
    AwaitingSignature = 0,

    /// <summary>The operator's signed transaction is stored; a worker will broadcast it.</summary>
    Signed = 1,

    /// <summary>The signed transaction has been broadcast to the chain.</summary>
    Broadcast = 2,

    /// <summary>Confirmed on-chain — the target hot wallet's float has increased.</summary>
    Confirmed = 3,

    /// <summary>Abandoned before broadcast (nothing left the chain).</summary>
    Failed = 4,
}
