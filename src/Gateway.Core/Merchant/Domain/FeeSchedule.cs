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
    public static FeeSchedule None { get; } = new(BigInteger.Zero, 0, BigInteger.Zero, 0);

    private FeeSchedule(BigInteger depositFeeFixed, int depositFeeBps, BigInteger withdrawalFee, int withdrawalFeeBps)
    {
        DepositFeeFixed = depositFeeFixed;
        DepositFeeBps = depositFeeBps;
        WithdrawalFee = withdrawalFee;
        WithdrawalFeeBps = withdrawalFeeBps;
    }

    public BigInteger DepositFeeFixed { get; }

    /// <summary>Deposit percentage in basis points. Bounded to <c>[0, MaxBps)</c> — a 100% deposit fee cannot be grossed up.</summary>
    public int DepositFeeBps { get; }

    public BigInteger WithdrawalFee { get; }

    /// <summary>Withdrawal percentage in basis points, <c>[0, MaxBps]</c>.</summary>
    public int WithdrawalFeeBps { get; }

    public static Result<FeeSchedule> Create(
        BigInteger depositFeeFixed, int depositFeeBps, BigInteger withdrawalFee, int withdrawalFeeBps)
    {
        if (depositFeeFixed < BigInteger.Zero || withdrawalFee < BigInteger.Zero)
            return Result.Failure<FeeSchedule>(MerchantErrors.AmountNegative);

        if (!MoneyLimits.IsStorable(depositFeeFixed) || !MoneyLimits.IsStorable(withdrawalFee))
            return Result.Failure<FeeSchedule>(MerchantErrors.AmountTooLarge);

        // A deposit fee of 100% (or more) makes the payer-on-top gross-up unsolvable, so deposit bps is
        // strictly below MaxBps; a withdrawal fee is deducted, so 100% is merely absurd, not impossible.
        if (depositFeeBps < 0 || depositFeeBps >= MaxBps)
            return Result.Failure<FeeSchedule>(MerchantErrors.FeeBpsInvalid);

        if (withdrawalFeeBps < 0 || withdrawalFeeBps > MaxBps)
            return Result.Failure<FeeSchedule>(MerchantErrors.FeeBpsInvalid);

        return Result.Success(new FeeSchedule(depositFeeFixed, depositFeeBps, withdrawalFee, withdrawalFeeBps));
    }

    /// <summary>Rehydrates from already-validated persisted columns. Persistence only — skips validation.</summary>
    internal static FeeSchedule FromTrusted(
        BigInteger depositFeeFixed, int depositFeeBps, BigInteger withdrawalFee, int withdrawalFeeBps) =>
        new(depositFeeFixed, depositFeeBps, withdrawalFee, withdrawalFeeBps);

    /// <summary>
    /// The platform fee taken from a deposit of <paramref name="receivedAmount"/> base units. Computed on
    /// the amount that actually arrived, so it needs no invoice — the Ledger can split any confirmed
    /// deposit independently.
    /// </summary>
    public BigInteger QuoteDepositFee(BigInteger receivedAmount) =>
        receivedAmount <= BigInteger.Zero
            ? BigInteger.Zero
            : DepositFeeFixed + receivedAmount * DepositFeeBps / MaxBps;

    /// <summary>The platform fee charged on a withdrawal of <paramref name="amount"/> base units.</summary>
    public BigInteger QuoteWithdrawalFee(BigInteger amount) =>
        amount <= BigInteger.Zero
            ? BigInteger.Zero
            : WithdrawalFee + amount * WithdrawalFeeBps / MaxBps;

    /// <summary>
    /// Payer-pays-on-top: the smallest gross the payer must send so the merchant nets at least
    /// <paramref name="netTarget"/> after the deposit fee. Solves <c>G − QuoteDepositFee(G) ≥ netTarget</c>
    /// exactly under integer floor arithmetic — the closed form seeds it, then two bounded nudges absorb
    /// the flooring residual so the payer is never over-asked by even one base unit.
    /// </summary>
    public Result<BigInteger> GrossUpForDeposit(BigInteger netTarget)
    {
        if (netTarget <= BigInteger.Zero)
            return Result.Failure<BigInteger>(MerchantErrors.AmountNegative);

        // G·(1 − bps/10000) − fixed = net  ⇒  G = (net + fixed)·10000 / (10000 − bps), rounded up.
        var denominator = MaxBps - DepositFeeBps; // > 0: Create bounds DepositFeeBps < MaxBps
        var numerator = (netTarget + DepositFeeFixed) * MaxBps;
        var gross = (numerator + denominator - 1) / denominator; // ceil

        // Nudge up until the merchant truly nets the target, then down to the minimal such gross.
        while (gross - QuoteDepositFee(gross) < netTarget)
            gross += 1;
        while (gross > BigInteger.One && gross - 1 - QuoteDepositFee(gross - 1) >= netTarget)
            gross -= 1;

        if (!MoneyLimits.IsStorable(gross))
            return Result.Failure<BigInteger>(MerchantErrors.AmountTooLarge);

        return Result.Success(gross);
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return DepositFeeFixed;
        yield return DepositFeeBps;
        yield return WithdrawalFee;
        yield return WithdrawalFeeBps;
    }
}
