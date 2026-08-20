using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.SharedKernel;
using Shouldly;
using Xunit;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Tests;

/// <summary>
/// The merchant-withdrawal (earnings cash-out) REQUEST rules — the part that differs from a user payout: the
/// destination is the whitelisted settlement wallet (never client-supplied), and the amount is gated by the
/// flat/% liquidity cap. The shared reserve → sign → broadcast → settle pipeline is covered by
/// <see cref="WithdrawalFlowTests"/> (a merchant withdrawal takes the identical path). Fakes stand in for the
/// repository, ledger, and merchant seams so these assertions are about the request rules alone.
/// </summary>
public sealed class MerchantWithdrawalServiceTests
{
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();
    private const string Settlement = "TSettlementWallet";
    private static readonly BigInteger Fee = BigInteger.Parse("100000");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (MerchantWithdrawalService Service, FakeRepo Repo) Compose(
        string? settlement = Settlement,
        MerchantWithdrawalCap? cap = null,
        BigInteger? balance = null,
        bool reserveSucceeds = true,
        bool canTransact = true,
        int settlementDelayDays = 0,
        BigInteger? settled = null,
        BigInteger? merchantThreshold = null)
    {
        var repo = new FakeRepo();
        var ledgerQuery = new FakeLedgerQuery(balance ?? BigInteger.Parse("10000000"), settled);
        var service = new MerchantWithdrawalService(
            repo,
            new StubPolicy(),
            new FakeMerchants(canTransact, settlementDelayDays),
            new FakeSettlements(settlement),
            new FakeCaps(cap ?? MerchantWithdrawalCap.None),
            new FakeFees(Fee),
            new FakeApprovalThreshold(merchantThreshold),
            new SettledBalanceGate(ledgerQuery, TimeProvider.System),
            new FakeLedger(reserveSucceeds),
            TimeProvider.System);
        return (service, repo);
    }

    private static MerchantWithdrawalCommand Command(BigInteger amount, string txnId = "cash-1") =>
        new(Merchant, Asset, Chain.Tron, amount, txnId);

    [Fact]
    public async Task A_cash_out_goes_to_the_settlement_wallet_with_the_withdrawal_fee_and_reserves()
    {
        var (service, repo) = Compose();

        var result = await service.RequestAsync(Command(BigInteger.Parse("3000000")), Ct);

        result.IsSuccess.ShouldBeTrue();
        var w = repo.Withdrawals.ShouldHaveSingleItem();
        w.Kind.ShouldBe(WithdrawalKind.Merchant);
        w.DestinationAddress.ShouldBe(Settlement);      // resolved server-side, never client-supplied
        w.Fee.ShouldBe(Fee);                            // the same schedule as a user payout
        w.Status.ShouldBe(WithdrawalStatus.Approved);   // reserved + below the approval threshold
    }

    [Fact]
    public async Task A_lowered_per_merchant_approval_threshold_holds_a_cash_out_for_approval()
    {
        // Config threshold is 5,000,000; the merchant lowers theirs to 1,000,000, so a 3,000,000 cash-out
        // now needs approval (the threshold applies to cash-outs exactly as to user payouts).
        var (service, repo) = Compose(merchantThreshold: BigInteger.Parse("1000000"));

        (await service.RequestAsync(Command(BigInteger.Parse("3000000")), Ct)).IsSuccess.ShouldBeTrue();
        repo.Withdrawals.ShouldHaveSingleItem().Status.ShouldBe(WithdrawalStatus.PendingApproval);
    }

    [Fact]
    public async Task A_cash_out_without_a_registered_settlement_wallet_is_rejected()
    {
        var (service, repo) = Compose(settlement: null);

        var result = await service.RequestAsync(Command(BigInteger.Parse("3000000")), Ct);

        result.Error!.Code.ShouldBe(WithdrawalErrors.SettlementWalletNotRegistered.Code);
        repo.Withdrawals.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_closed_or_inactive_merchant_cannot_cash_out()
    {
        var (service, _) = Compose(canTransact: false);

        (await service.RequestAsync(Command(BigInteger.Parse("3000000")), Ct))
            .Error!.Code.ShouldBe(WithdrawalErrors.MerchantCannotTransact.Code);
    }

    [Fact]
    public async Task A_cash_out_over_the_flat_cap_is_rejected()
    {
        var (service, _) = Compose(cap: new MerchantWithdrawalCap(BigInteger.Parse("1000000"), 0));

        (await service.RequestAsync(Command(BigInteger.Parse("2000000")), Ct))
            .Error!.Code.ShouldBe(WithdrawalErrors.ExceedsMerchantWithdrawalLimit.Code);
    }

    [Fact]
    public async Task A_cash_out_over_the_percent_cap_is_rejected()
    {
        // 50% of a 10,000,000 balance = 5,000,000; a 6,000,000 cash-out exceeds it.
        var (service, _) = Compose(cap: new MerchantWithdrawalCap(null, 5000), balance: BigInteger.Parse("10000000"));

        (await service.RequestAsync(Command(BigInteger.Parse("6000000")), Ct))
            .Error!.Code.ShouldBe(WithdrawalErrors.ExceedsMerchantWithdrawalLimit.Code);
    }

    [Fact]
    public async Task The_most_restrictive_of_flat_and_percent_applies()
    {
        // flat 1,000,000 vs 50% of 10,000,000 = 5,000,000 ⇒ effective cap 1,000,000.
        var (service, _) = Compose(cap: new MerchantWithdrawalCap(BigInteger.Parse("1000000"), 5000), balance: BigInteger.Parse("10000000"));

        (await service.RequestAsync(Command(BigInteger.Parse("1500000")), Ct))
            .Error!.Code.ShouldBe(WithdrawalErrors.ExceedsMerchantWithdrawalLimit.Code);
        (await service.RequestAsync(Command(BigInteger.Parse("1000000"), "cash-2"), Ct))
            .IsSuccess.ShouldBeTrue(); // exactly at the cap → allowed
    }

    [Fact]
    public async Task With_no_cap_a_cash_out_up_to_the_balance_is_allowed()
    {
        var (service, _) = Compose(cap: MerchantWithdrawalCap.None);

        (await service.RequestAsync(Command(BigInteger.Parse("9999999")), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_resent_reference_is_rejected_and_never_cashed_out_twice()
    {
        var (service, repo) = Compose();

        (await service.RequestAsync(Command(BigInteger.Parse("3000000"), "dupe"), Ct)).IsSuccess.ShouldBeTrue();
        var second = await service.RequestAsync(Command(BigInteger.Parse("3000000"), "dupe"), Ct);

        second.Error!.Code.ShouldBe(WithdrawalErrors.DuplicateReference.Code);
        repo.Withdrawals.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_insufficient_balance_reserve_fails_the_cash_out()
    {
        var (service, _) = Compose(reserveSucceeds: false);

        (await service.RequestAsync(Command(BigInteger.Parse("3000000")), Ct))
            .Error!.Code.ShouldBe(WithdrawalErrors.InsufficientBalance.Code);
    }

    [Fact]
    public async Task A_cash_out_over_the_settled_balance_is_rejected()
    {
        // Settlement period active: only 1,000,000 has matured though the total balance is 10,000,000.
        var (service, repo) = Compose(
            settlementDelayDays: 2, settled: BigInteger.Parse("1000000"), balance: BigInteger.Parse("10000000"));

        (await service.RequestAsync(Command(BigInteger.Parse("2000000")), Ct))
            .Error!.Code.ShouldBe(WithdrawalErrors.ExceedsSettledBalance.Code);
        repo.Withdrawals.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_percent_cap_is_taken_against_the_settled_balance_not_the_total()
    {
        // Total 10,000,000 but only 2,000,000 settled; a 50% cap ⇒ 1,000,000. A 1,500,000 cash-out is within
        // settled yet over the cap computed on settled — proving the cap base is settled, not total.
        var (service, _) = Compose(
            cap: new MerchantWithdrawalCap(null, 5000), settlementDelayDays: 2,
            settled: BigInteger.Parse("2000000"), balance: BigInteger.Parse("10000000"));

        (await service.RequestAsync(Command(BigInteger.Parse("1500000")), Ct))
            .Error!.Code.ShouldBe(WithdrawalErrors.ExceedsMerchantWithdrawalLimit.Code);
    }

    // ── fakes ──

    private sealed class FakeRepo : IWithdrawalRepository
    {
        public readonly List<WithdrawalEntity> Withdrawals = [];

        public Task<WithdrawalEntity?> FindByMerchantTransactionIdAsync(
            Guid merchantId, WithdrawalKind kind, string merchantTransactionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Withdrawals.SingleOrDefault(
                w => w.MerchantId == merchantId && w.Kind == kind && w.MerchantTransactionId == merchantTransactionId));

        public Task<WithdrawalRecordOutcome> AddIfNewAsync(WithdrawalEntity withdrawal, CancellationToken cancellationToken = default)
        {
            if (Withdrawals.Any(w => w.MerchantId == withdrawal.MerchantId && w.Kind == withdrawal.Kind && w.MerchantTransactionId == withdrawal.MerchantTransactionId))
                return Task.FromResult(WithdrawalRecordOutcome.Duplicate);

            Withdrawals.Add(withdrawal);
            return Task.FromResult(WithdrawalRecordOutcome.Recorded);
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);

        public Task<WithdrawalEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WithdrawalEntity>> GetByStatusesAsync(IReadOnlyCollection<WithdrawalStatus> statuses, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetInFlightSourceWalletIdsAsync(Chain chain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetWalletLastUsedAsync(IReadOnlyCollection<Guid> walletIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TrySaveSignedAsync(WithdrawalEntity withdrawal, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubPolicy : IWithdrawalPolicyProvider
    {
        public WithdrawalPolicy For(Chain chain) =>
            new(Minimum: BigInteger.Zero, Maximum: null, Fee: BigInteger.Zero,
                ApprovalThreshold: BigInteger.Parse("5000000"), Confirmations: 1);
    }

    private sealed class FakeMerchants(bool canTransact, int settlementDelayDays = 0) : IMerchantDirectory
    {
        public Task<MerchantSummary?> FindByIdAsync(Guid merchantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MerchantSummary?>(new MerchantSummary(merchantId, "ACME", "Acme", null, canTransact, settlementDelayDays));

        public Task<MerchantSummary?> FindByCodeAsync(string merchantCode, CancellationToken cancellationToken = default) =>
            Task.FromResult<MerchantSummary?>(null);
    }

    private sealed class FakeSettlements(string? address) : IMerchantSettlementDirectory
    {
        public Task<string?> FindSettlementAddressAsync(Guid merchantId, Chain chain, CancellationToken cancellationToken = default) =>
            Task.FromResult(address);
    }

    private sealed class FakeCaps(MerchantWithdrawalCap cap) : IMerchantWithdrawalCap
    {
        public Task<MerchantWithdrawalCap> GetAsync(Guid merchantId, Guid assetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(cap);
    }

    private sealed class FakeApprovalThreshold(BigInteger? threshold) : IMerchantApprovalThreshold
    {
        public Task<BigInteger?> GetAsync(Guid merchantId, Guid assetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(threshold);
    }

    private sealed class FakeFees(BigInteger fee) : IMerchantFeeSchedule
    {
        public Task<BigInteger> QuoteDepositFeeAsync(Guid merchantId, Guid assetId, BigInteger receivedAmount, CancellationToken cancellationToken = default) =>
            Task.FromResult(BigInteger.Zero);

        public Task<BigInteger> QuoteWithdrawalFeeAsync(Guid merchantId, Guid assetId, BigInteger amount, CancellationToken cancellationToken = default) =>
            Task.FromResult(fee);

        public Task<Result<BigInteger>> GrossUpDepositAsync(Guid merchantId, Guid assetId, BigInteger netTarget, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(netTarget));
    }

    private sealed class FakeLedgerQuery(BigInteger balance, BigInteger? settled = null) : ILedgerQuery
    {
        public Task<BigInteger> GetMerchantBalanceAsync(Guid merchantId, Guid assetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(balance);

        public Task<BigInteger> GetMerchantSettledBalanceAsync(
            Guid merchantId, Guid assetId, DateTimeOffset unmaturedCutoffUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(settled ?? balance);

        public Task<BigInteger> GetTreasuryHoldingAsync(Guid assetId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(IReadOnlyList<MerchantJournalView> Items, int TotalCount)> GetJournalsAsync(
            Guid? merchantId, Guid? referenceId, DateTimeOffset? fromDate, DateTimeOffset? toDate, int page, int pageSize, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeLedger(bool succeeds) : IWithdrawalLedger
    {
        public Task<Result> ReserveAsync(ReserveWithdrawalRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(succeeds ? Result.Success() : Result.Failure(Error.Conflict("test.reserve_failed", "insufficient")));
    }
}
