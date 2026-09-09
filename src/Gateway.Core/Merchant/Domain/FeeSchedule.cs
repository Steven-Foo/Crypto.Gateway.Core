using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

/// <summary>
/// A merchant's per-asset pricing: a fixed base-unit component plus a percentage in basis points, for
/// both deposits and withdrawals. This is <b>pricing</b>, deliberately separate from operational limits
/// (min/max/approval), and it owns the money math so the arithmetic lives in exactly one, unit-testable
/// place (§14).
///
/// All amounts are unsigned integer base units; percentages are basis points (1 bp = 0.01%, so
/// <see cref="MaxBps"/> = 100%). The fee is <c>fixed + ⌊amount·bps / 10000⌋</c> — the division floors,
/// which is documented and deliberate: the platform never rounds a fee <em>up</em> against the merchant.
/// </summary>
public sealed class FeeSchedule : ValueObject
{
    /// <summary>10 000 basis points = 100%.</summary>
    public const int MaxBps = 10_000;

    /// <summary>The no-fee schedule — an unpriced merchant is charged nothing (a documented ops gap, never an overcharge).</summary>
    public static FeeSchedule None { get; } = new(BigInteger.Zero, 0, BigInteger.Zero, 0, BigInteger.Zero, 0, BigInteger.Zero, BigInteger.Zero);

    private FeeSchedule(
        BigInteger depositFeeFixed, int depositFeeBps, BigInteger withdrawalFee, int withdrawalFeeBps,
        BigInteger topUpFeeFixed, int topUpFeeBps, BigInteger minimumDepositFee, BigInteger minimumWithdrawalFee)
    {
        DepositFeeFixed = depositFeeFixed;
        DepositFeeBps = depositFeeBps;
        WithdrawalFee = withdrawalFee;
        WithdrawalFeeBps = withdrawalFeeBps;
        TopUpFeeFixed = topUpFeeFixed;
        TopUpFeeBps = topUpFeeBps;
        MinimumDepositFee = minimumDepositFee;
        MinimumWithdrawalFee = minimumWithdrawalFee;
    }

    public BigInteger DepositFeeFixed { get; }

    /// <summary>Deposit percentage in basis points, <c>[0, MaxBps]</c>.</summary>
    public int DepositFeeBps { get; }

    public BigInteger WithdrawalFee { get; }

    /// <summary>Withdrawal percentage in basis points, <c>[0, MaxBps]</c>.</summary>
    public int WithdrawalFeeBps { get; }

    /// <summary>
    /// Fixed component of the fee on a <b>merchant top-up</b> — the merchant funding its own balance by
    /// sending crypto to its deposit address. Kept separate from the deposit fee on purpose: that one prices
    /// a customer's payment, and a merchant funding its own float should not be charged the same rate.
    /// </summary>
    public BigInteger TopUpFeeFixed { get; }

    /// <summary>
    /// Top-up percentage in basis points, <c>[0, MaxBps]</c>. Deducted from what arrives, exactly like the
    /// deposit fee — the merchant sends the invoiced amount and is credited that minus this.
    /// </summary>
    public int TopUpFeeBps { get; }

    /// <summary>Floor under the calculated deposit fee (fixed + %) — 最低手续费. Zero = no floor. See
    /// <see cref="QuoteDepositFeeDetailed"/> for the <c>max(calculated, minimum)</c> arithmetic.</summary>
    public BigInteger MinimumDepositFee { get; }

    /// <summary>Floor under the calculated withdrawal fee (fixed + %) — 最低手续费. Zero = no floor. See
    /// <see cref="QuoteWithdrawalFeeDetailed"/>.</summary>
    public BigInteger MinimumWithdrawalFee { get; }

    /// <summary>
    /// Prices deposits and withdrawals, leaving the merchant top-up free and no minimum-fee floor. This is
    /// the overload every caller that predates top-up/minimum pricing uses.
    /// </summary>
    public static Result<FeeSchedule> Create(
        BigInteger depositFeeFixed, int depositFeeBps, BigInteger withdrawalFee, int withdrawalFeeBps) =>
        Create(depositFeeFixed, depositFeeBps, withdrawalFee, withdrawalFeeBps, BigInteger.Zero, 0, BigInteger.Zero, BigInteger.Zero);

    public static Result<FeeSchedule> Create(
        BigInteger depositFeeFixed, int depositFeeBps, BigInteger withdrawalFee, int withdrawalFeeBps,
        BigInteger topUpFeeFixed, int topUpFeeBps) =>
        Create(depositFeeFixed, depositFeeBps, withdrawalFee, withdrawalFeeBps, topUpFeeFixed, topUpFeeBps, BigInteger.Zero, BigInteger.Zero);

    public static Result<FeeSchedule> Create(
        BigInteger depositFeeFixed, int depositFeeBps, BigInteger withdrawalFee, int withdrawalFeeBps,
        BigInteger topUpFeeFixed, int topUpFeeBps, BigInteger minimumDepositFee, BigInteger minimumWithdrawalFee)
    {
        if (depositFeeFixed < BigInteger.Zero || withdrawalFee < BigInteger.Zero || topUpFeeFixed < BigInteger.Zero
            || minimumDepositFee < BigInteger.Zero || minimumWithdrawalFee < BigInteger.Zero)
            return Result.Failure<FeeSchedule>(MerchantErrors.AmountNegative);

        if (!MoneyLimits.IsStorable(depositFeeFixed) || !MoneyLimits.IsStorable(withdrawalFee)
            || !MoneyLimits.IsStorable(topUpFeeFixed) || !MoneyLimits.IsStorable(minimumDepositFee)
            || !MoneyLimits.IsStorable(minimumWithdrawalFee))
            return Result.Failure<FeeSchedule>(MerchantErrors.AmountTooLarge);

        // Every fee is DEDUCTED from what arrives (there is no payer-on-top gross-up any more), so all three
        // share the same bound: 0–100% inclusive. A 100% fee is merely absurd, not arithmetically impossible —
        // previously the deposit rate had to stay strictly under 100% to keep the gross-up solvable.
        if (topUpFeeBps < 0 || topUpFeeBps > MaxBps)
            return Result.Failure<FeeSchedule>(MerchantErrors.FeeBpsInvalid);

        if (depositFeeBps < 0 || depositFeeBps > MaxBps)
            return Result.Failure<FeeSchedule>(MerchantErrors.FeeBpsInvalid);

        if (withdrawalFeeBps < 0 || withdrawalFeeBps > MaxBps)
            return Result.Failure<FeeSchedule>(MerchantErrors.FeeBpsInvalid);

        return Result.Success(new FeeSchedule(
            depositFeeFixed, depositFeeBps, withdrawalFee, withdrawalFeeBps, topUpFeeFixed, topUpFeeBps,
            minimumDepositFee, minimumWithdrawalFee));
    }

    /// <summary>Rehydrates from already-validated persisted columns. Persistence only — skips validation.</summary>
    internal static FeeSchedule FromTrusted(
        BigInteger depositFeeFixed, int depositFeeBps, BigInteger withdrawalFee, int withdrawalFeeBps,
        BigInteger topUpFeeFixed = default, int topUpFeeBps = 0,
        BigInteger minimumDepositFee = default, BigInteger minimumWithdrawalFee = default) =>
        new(depositFeeFixed, depositFeeBps, withdrawalFee, withdrawalFeeBps, topUpFeeFixed, topUpFeeBps,
            minimumDepositFee, minimumWithdrawalFee);

    /// <summary>
    /// The platform fee taken from a deposit of <paramref name="receivedAmount"/> base units:
    /// <c>max(fixed + amount×bps/10000, minimum)</c> — 实收手续费. Computed on the amount that actually
    /// arrived, so it needs no invoice — the Ledger can split any confirmed deposit independently.
    /// </summary>
    public BigInteger QuoteDepositFee(BigInteger receivedAmount) => QuoteDepositFeeDetailed(receivedAmount).Fee;

    /// <summary>Same as <see cref="QuoteDepositFee"/>, plus whether the 最低手续费 floor actually kicked in
    /// (the calculated fixed+% fee came out below the minimum) — the one place this arithmetic lives, so
    /// every caller that needs to know/record it (fee transparency on the deposit record) shares it.</summary>
    public (BigInteger Fee, bool MinimumApplied) QuoteDepositFeeDetailed(BigInteger receivedAmount)
    {
        if (receivedAmount <= BigInteger.Zero)
            return (BigInteger.Zero, false);

        var calculated = DepositFeeFixed + receivedAmount * DepositFeeBps / MaxBps;
        return calculated < MinimumDepositFee ? (MinimumDepositFee, true) : (calculated, false);
    }

    /// <summary>The platform fee charged on a withdrawal of <paramref name="amount"/> base units:
    /// <c>max(fixed + amount×bps/10000, minimum)</c>.</summary>
    public BigInteger QuoteWithdrawalFee(BigInteger amount) => QuoteWithdrawalFeeDetailed(amount).Fee;

    /// <summary>Same as <see cref="QuoteWithdrawalFee"/>, plus whether the 最低手续费 floor kicked in.</summary>
    public (BigInteger Fee, bool MinimumApplied) QuoteWithdrawalFeeDetailed(BigInteger amount)
    {
        if (amount <= BigInteger.Zero)
            return (BigInteger.Zero, false);

        var calculated = WithdrawalFee + amount * WithdrawalFeeBps / MaxBps;
        return calculated < MinimumWithdrawalFee ? (MinimumWithdrawalFee, true) : (calculated, false);
    }

    /// <summary>
    /// The platform fee taken from a <b>merchant top-up</b> of <paramref name="receivedAmount"/> base units.
    ///
    /// <para>Computed on what actually arrived, exactly like the deposit fee. Kept a separate rate because a
    /// merchant funding its own float should not be priced like a customer payment.</para>
    /// </summary>
    public BigInteger QuoteTopUpFee(BigInteger receivedAmount) =>
        receivedAmount <= BigInteger.Zero
            ? BigInteger.Zero
            : TopUpFeeFixed + receivedAmount * TopUpFeeBps / MaxBps;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return DepositFeeFixed;
        yield return DepositFeeBps;
        yield return WithdrawalFee;
        yield return WithdrawalFeeBps;
        yield return TopUpFeeFixed;
        yield return TopUpFeeBps;
        yield return MinimumDepositFee;
        yield return MinimumWithdrawalFee;
    }
}
