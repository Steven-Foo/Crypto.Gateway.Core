namespace CryptoPaymentEngine.SharedKernel;

/// <summary>
/// Shared vocabulary, not business logic (§4.8): the set of chains the platform recognises.
/// Chain-specific behaviour lives in the Blockchain module's adapters, never here.
/// Bitcoin is deliberately absent — it is UTXO-model and needs <c>IUtxoSource</c> plus a
/// different transaction builder (§8), which is real design work, not a new enum member.
/// </summary>
public enum Chain
{
    Tron = 1,
    Ethereum = 2,
    Solana = 3,
}
