using System.Numerics;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain;

/// <summary>
/// Per-chain deposit policy. <see cref="MinDeposit"/> is in unsigned base units (converted from the
/// human-readable config value at load, never compared in display units). The policy decides two
/// things and nothing else: is this amount worth crediting, and is it creditable yet.
/// </summary>
public sealed record DepositPolicy(CreditStrategy CreditStrategy, int RequiredConfirmations, BigInteger MinDeposit)
{
    /// <summary>An amount at or above the dust floor is worth recording as a real deposit.</summary>
    public bool MeetsMinimum(BigInteger amount) => amount >= MinDeposit;

    /// <summary>
    /// Whether the deposit may now be credited. Confirmation chains compare depth; finality chains ask
    /// the chain's own irreversibility signal.
    /// </summary>
    public bool IsCreditable(int confirmations, bool isFinalized) => CreditStrategy switch
    {
        CreditStrategy.Confirmations => confirmations >= RequiredConfirmations,
        CreditStrategy.Finalized => isFinalized,
        _ => false,
    };
}
