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
/// The USER-payout request rules, focused on the per-merchant min/max <b>override</b> of the platform config
/// limits (config default = min 1000, max 1_000_000). A set merchant bound fully overrides — raising OR
/// lowering; an unset (null) bound falls back to config. Fakes isolate the limit resolution; the shared
/// reserve→send pipeline is covered by <see cref="WithdrawalFlowTests"/>.
/// </summary>
public sealed class WithdrawalRequestServiceTests
{
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly BigInteger ConfigMin = new(1_000);
    private static readonly BigInteger ConfigMax = new(1_000_000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WithdrawalRequestService Compose(MerchantWithdrawalLimits? limits = null, BigInteger? merchantThreshold = null)
    {
        var ledger = new AmpleLedgerQuery();
        return new WithdrawalRequestService(
            new FakeRepo(),
            new StubPolicy(),
            new FakeMerchants(),
            new FakeFees(),
            new FakeLimits(limits ?? MerchantWithdrawalLimits.None),
            new FakeApprovalThreshold(merchantThreshold),
            new SettledBalanceGate(ledger, TimeProvider.System),
            new FakeLedger(),
            TimeProvider.System);
    }

    private static RequestWithdrawalCommand Command(BigInteger amount, string txn = "w-1") =>
        new(Merchant, Asset, Chain.Tron, "TDest", amount, txn);

    [Fact]
    public async Task Config_min_max_apply_when_the_merchant_has_no_override()
    {
        var service = Compose(); // limits None ⇒ config
        (await service.RequestAsync(Command(new BigInteger(500)), Ct))
            .Error!.Code.ShouldBe(WithdrawalErrors.BelowMinimum.Code);          // < config min 1000
        (await service.RequestAsync(Command(new BigInteger(2_000_000), "w-2"), Ct))
            .Error!.Code.ShouldBe(WithdrawalErrors.AboveMaximum.Code);          // > config max 1_000_000
        (await service.RequestAsync(Command(new BigInteger(5_000), "w-3"), Ct))
            .IsSuccess.ShouldBeTrue();                                          // within config range
    }

    [Fact]
    public async Task A_per_merchant_minimum_overrides_config_including_lowering_it()
    {
        // Merchant min 100 < config 1000 ⇒ a 500 payout now passes; 50 is still below the merchant's own min.
        var service = Compose(new MerchantWithdrawalLimits(new BigInteger(100), null));
        (await service.RequestAsync(Command(new BigInteger(500)), Ct)).IsSuccess.ShouldBeTrue();
        (await service.RequestAsync(Command(new BigInteger(50), "w-2"), Ct))
            .Error!.Code.ShouldBe(WithdrawalErrors.BelowMinimum.Code);
    }

    [Fact]
    public async Task A_per_merchant_maximum_overrides_config_by_tightening_it()
    {
        // Merchant max 3000 < config 1_000_000 ⇒ a 5000 payout is now over the merchant's own max.
        var service = Compose(new MerchantWithdrawalLimits(null, new BigInteger(3_000)));
        (await service.RequestAsync(Command(new BigInteger(5_000)), Ct))
            .Error!.Code.ShouldBe(WithdrawalErrors.AboveMaximum.Code);
        (await service.RequestAsync(Command(new BigInteger(2_500), "w-2"), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_lowered_per_merchant_approval_threshold_sends_a_payout_above_it_to_pending_approval()
    {
        // Config threshold is 1e9; the merchant lowers theirs to 10_000, so a 50_000 payout now needs approval.
        var result = await Compose(merchantThreshold: new BigInteger(10_000)).RequestAsync(Command(new BigInteger(50_000)), Ct);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe(WithdrawalStatus.PendingApproval.ToString());
    }

    [Fact]
    public async Task At_or_below_the_per_merchant_approval_threshold_auto_approves()
    {
        var result = await Compose(merchantThreshold: new BigInteger(1_000_000)).RequestAsync(Command(new BigInteger(50_000)), Ct);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe(WithdrawalStatus.Approved.ToString());
    }

    // ── fakes ──

    private sealed class FakeRepo : IWithdrawalRepository
    {
        private readonly List<WithdrawalEntity> _withdrawals = [];

        public Task<WithdrawalEntity?> FindByMerchantTransactionIdAsync(
            Guid merchantId, WithdrawalKind kind, string merchantTransactionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_withdrawals.SingleOrDefault(
                w => w.MerchantId == merchantId && w.Kind == kind && w.MerchantTransactionId == merchantTransactionId));

        public Task<WithdrawalRecordOutcome> AddIfNewAsync(WithdrawalEntity withdrawal, CancellationToken cancellationToken = default)
        {
            if (_withdrawals.Any(w => w.MerchantId == withdrawal.MerchantId && w.Kind == withdrawal.Kind && w.MerchantTransactionId == withdrawal.MerchantTransactionId))
                return Task.FromResult(WithdrawalRecordOutcome.Duplicate);
            _withdrawals.Add(withdrawal);
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
            new(Minimum: ConfigMin, Maximum: ConfigMax, Fee: BigInteger.Zero,
                ApprovalThreshold: BigInteger.Parse("1000000000"), Confirmations: 1);
    }

    private sealed class FakeMerchants : IMerchantDirectory
    {
        public Task<MerchantSummary?> FindByIdAsync(Guid merchantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MerchantSummary?>(new MerchantSummary(merchantId, "ACME", "Acme", null, CanTransact: true));

        public Task<MerchantSummary?> FindByCodeAsync(string merchantCode, CancellationToken cancellationToken = default) =>
            Task.FromResult<MerchantSummary?>(null);

        public Task<IReadOnlyDictionary<Guid, string>> GetNamesByIdsAsync(
            IReadOnlyList<Guid> merchantIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task<IReadOnlyList<Guid>> SearchIdsByNameAsync(string nameContains, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class FakeFees : IMerchantFeeSchedule
    {
        public Task<BigInteger> QuoteDepositFeeAsync(Guid merchantId, Guid assetId, BigInteger receivedAmount, CancellationToken cancellationToken = default) =>
            Task.FromResult(BigInteger.Zero);
        public Task<BigInteger> QuoteWithdrawalFeeAsync(Guid merchantId, Guid assetId, BigInteger amount, CancellationToken cancellationToken = default) =>
            Task.FromResult(BigInteger.Zero);
        public Task<Result<BigInteger>> GrossUpDepositAsync(Guid merchantId, Guid assetId, BigInteger netTarget, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(netTarget));
    }

    private sealed class FakeLimits(MerchantWithdrawalLimits limits) : IMerchantWithdrawalLimits
    {
        public Task<MerchantWithdrawalLimits> GetAsync(Guid merchantId, Guid assetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(limits);
    }

    private sealed class FakeApprovalThreshold(BigInteger? threshold) : IMerchantApprovalThreshold
    {
        public Task<BigInteger?> GetAsync(Guid merchantId, Guid assetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(threshold);
    }

    private sealed class AmpleLedgerQuery : ILedgerQuery
    {
        private static readonly BigInteger Ample = BigInteger.Parse("1000000000000");
        public Task<BigInteger> GetMerchantBalanceAsync(Guid merchantId, Guid assetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Ample);
        public Task<BigInteger> GetMerchantSettledBalanceAsync(Guid merchantId, Guid assetId, DateTimeOffset unmaturedCutoffUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(Ample);
        public Task<BigInteger> GetTreasuryHoldingAsync(Guid assetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(IReadOnlyList<MerchantJournalView> Items, int TotalCount)> GetJournalsAsync(
            Guid? merchantId, Guid? referenceId, DateTimeOffset? fromDate, DateTimeOffset? toDate, int page, int pageSize, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeLedger : IWithdrawalLedger
    {
        public Task<Result> ReserveAsync(ReserveWithdrawalRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }
}
