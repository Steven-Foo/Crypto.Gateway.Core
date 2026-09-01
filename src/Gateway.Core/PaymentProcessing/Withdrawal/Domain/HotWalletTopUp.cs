using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;

/// <summary>
/// A record of an operations admin moving company funds INTO a hot withdrawal wallet, so the automated payout
/// pipeline has enough on-chain balance to keep paying end-users.
///
/// <para><b>This is a record of a transfer that already happened, not an instruction to make one.</b> The
/// admin sends the funds themselves, from a company wallet outside platform custody, and then records the
/// resulting transaction hash here. The system never holds the source key and never broadcasts anything for
/// this flow (§10).</para>
///
/// <para><b>Ledger impact:</b> <c>Dr TreasuryAsset / Cr WithdrawalWalletTopUp</c>. Custody genuinely rises —
/// we now hold more crypto in an address we watch, so reconciliation would otherwise report drift equal to
/// every top-up ever made. The credit is a System-owned contribution account, never a merchant account, so a
/// top-up cannot create merchant money: <c>merchant withdrawable = deposits − fees − settlements − payouts</c>
/// is structurally unaffected (§14).</para>
///
/// <para>The transaction hash is verified on-chain before this record is created — it must exist, be
/// confirmed, and carry the expected destination, asset and amount — so a typo or a mispaste can never inflate
/// recorded custody against a transfer that did not happen.</para>
/// </summary>
public sealed class HotWalletTopUp : Entity<Guid>
{
    private HotWalletTopUp(
        Guid id, Chain chain, Guid assetId, Guid targetWalletId, string targetAddress, BigInteger amount,
        string transactionHash, string? sourceAddress, string recordedBy, DateTimeOffset now) : base(id)
    {
        Chain = chain;
        AssetId = assetId;
        TargetWalletId = targetWalletId;
        TargetAddress = targetAddress;
        Amount = amount;
        TransactionHash = transactionHash;
        SourceAddress = sourceAddress;
        RecordedBy = recordedBy;
        RecordedAt = now;
    }

    private HotWalletTopUp() : base(Guid.Empty)
    {
    }

    public Chain Chain { get; private set; }
    public Guid AssetId { get; private set; }

    /// <summary>The hot-pool wallet that received the funds — chosen by the admin, who decides which wallet
    /// most needs topping up.</summary>
    public Guid TargetWalletId { get; private set; }

    public string TargetAddress { get; private set; } = null!;
    public BigInteger Amount { get; private set; }

    /// <summary>The verified on-chain hash. Unique across all top-ups — the DB index is the arbiter against
    /// the same transfer being recorded twice (§7.3), not application logic.</summary>
    public string TransactionHash { get; private set; } = null!;

    /// <summary>The company wallet the funds came from. Recorded for the audit trail; deliberately not
    /// constrained, since it sits outside platform custody and the admin chooses it.</summary>
    public string? SourceAddress { get; private set; }

    public string RecordedBy { get; private set; } = null!;
    public DateTimeOffset RecordedAt { get; private set; }

    public static Result<HotWalletTopUp> Record(
        Chain chain,
        Guid assetId,
        Guid targetWalletId,
        string targetAddress,
        BigInteger amount,
        string transactionHash,
        string? sourceAddress,
        string recordedBy,
        DateTimeOffset now)
    {
        if (amount <= BigInteger.Zero || !MoneyLimits.IsStorable(amount))
            return Result.Failure<HotWalletTopUp>(WithdrawalErrors.AmountNotPositive);

        if (assetId == Guid.Empty || targetWalletId == Guid.Empty || string.IsNullOrWhiteSpace(targetAddress))
            return Result.Failure<HotWalletTopUp>(WithdrawalErrors.OwnerRequired);

        if (string.IsNullOrWhiteSpace(transactionHash))
            return Result.Failure<HotWalletTopUp>(WithdrawalErrors.TransactionHashRequired);

        if (string.IsNullOrWhiteSpace(recordedBy))
            return Result.Failure<HotWalletTopUp>(WithdrawalErrors.OwnerRequired);

        var topUp = new HotWalletTopUp(
            Guid.CreateVersion7(), chain, assetId, targetWalletId, targetAddress.Trim(), amount,
            transactionHash.Trim(), string.IsNullOrWhiteSpace(sourceAddress) ? null : sourceAddress.Trim(),
            recordedBy.Trim(), now);

        // The Ledger books this from the event, via the outbox — the same durable, idempotent path every other
        // ledger-affecting change in this module uses. Posting inline from the service instead would reach
        // across into the Ledger's Application layer (§4.5) and leave a window where a crash between the write
        // and the posting understated custody with nothing to retry from.
        topUp.Raise(new Events.HotWalletToppedUp(
            Guid.CreateVersion7(), now, topUp.Id, assetId, targetWalletId, topUp.TargetAddress,
            amount.ToString(System.Globalization.CultureInfo.InvariantCulture), topUp.TransactionHash, now));

        return Result.Success(topUp);
    }
}
