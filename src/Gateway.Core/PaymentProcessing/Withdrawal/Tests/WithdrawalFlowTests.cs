using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Treasury;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Handlers;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Signing;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Events;
using CryptoPaymentEngine.Infrastructure.Locking;
using CryptoPaymentEngine.Infrastructure.Outbox;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Tests;

/// <summary>
/// The whole money-out path on a real SQL Server: request → ledger reserve (merchant debited) → build →
/// sign → broadcast → confirm → ledger settle (funds leave custody, fee → revenue). Plus the guard paths:
/// insufficient balance is refused with no debit, rejection releases the reserve, and the request is
/// idempotent. Signing goes through the (fake) <see cref="ISigner"/> port — no key touches the flow.
/// </summary>
public sealed class WithdrawalFlowTests : IAsyncLifetime
{
    private const string DbName = "CpeWithdrawalFlowTests";
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly BigInteger Fee = BigInteger.Parse("100000");
    private static readonly WithdrawalPolicy Policy =
        new(Minimum: BigInteger.Zero, Maximum: null, Fee: Fee, ApprovalThreshold: BigInteger.Parse("5000000"), Confirmations: 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private ServiceProvider _provider = null!;
    private InMemoryBalanceReader _hotFloat = null!;
    private StubEnergy _energy = null!;

    private const string HotWalletAddress = "THotWallet";
    private static readonly Guid HotWalletId = Guid.CreateVersion7();
    private static readonly BigInteger AmpleFloat = BigInteger.Parse("1000000000000");

    private void SetHotFloat(BigInteger amount) => _hotFloat.Set(Chain.Tron, HotWalletAddress, Asset, amount);

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDistributedLockFactory, NoOpLock>();

        services.AddDbContext<WithdrawalDbContext>(o => o.UseSqlServer(ConnectionString).UseBigIntegerMoney());
        services.AddDbContext<LedgerDbContext>(o => o.UseSqlServer(ConnectionString).UseBigIntegerMoney());

        // Ledger
        services.AddScoped<ILedgerAccountStore, LedgerAccountStore>();
        services.AddScoped<ILedgerPostingStore, LedgerPostingStore>();
        services.AddScoped<LedgerPoster>();
        services.AddScoped<ILedgerPoster>(sp => sp.GetRequiredService<LedgerPoster>());
        services.AddScoped<IWithdrawalLedger>(sp => sp.GetRequiredService<LedgerPoster>());
        services.AddScoped<IIntegrationEventHandler<WithdrawalConfirmed>, WithdrawalConfirmedHandler>();
        services.AddScoped<IIntegrationEventHandler<WithdrawalFailed>, WithdrawalFailedHandler>();
        services.AddScoped<IEventBus, InProcessEventBus>();

        // Withdrawal + fakes
        services.AddScoped<IWithdrawalRepository, WithdrawalRepository>();
        services.AddScoped<IWithdrawalDirectory, WithdrawalDirectory>();
        services.AddScoped<IWithdrawalRequestService, WithdrawalRequestService>();
        services.AddScoped<IWithdrawalApprovalService, WithdrawalApprovalService>();
        services.AddScoped<IWithdrawalFundingService, WithdrawalFundingService>();
        services.AddScoped<WithdrawalProcessingService>();
        services.AddScoped<WithdrawalConfirmationService>();
        services.AddSingleton(new GasAccountingOptions()); // 5c: empty ⇒ no gas journal (in-memory engine charges no fee anyway)
        services.AddSingleton<IWithdrawalPolicyProvider>(new StubPolicy());
        // Real pool allocator over a stub single-wallet pool: exercises allocation + lease-until-confirmed
        // (a 1-wallet pool serializes exactly like the old single wallet). The balance reader below funds it.
        services.AddSingleton<ITreasuryHotWalletDirectory>(new StubTreasuryPool(HotWalletId, HotWalletAddress));
        services.AddScoped<IHotWalletAllocator, HotWalletAllocator>();
        services.AddSingleton<IMerchantDirectory>(new FakeMerchants());
        services.AddSingleton<IMerchantFeeSchedule>(new FakeFees(Fee));
        services.AddSingleton<InMemoryTransactionEngine>();
        services.AddSingleton<ITransactionBuilder>(sp => sp.GetRequiredService<InMemoryTransactionEngine>());
        services.AddSingleton<ITransactionBroadcaster>(sp => sp.GetRequiredService<InMemoryTransactionEngine>());
        services.AddSingleton<IChainStatusReader>(new StubChainStatus());
        services.AddSingleton<ISigner, InMemorySigner>();

        // The physical float gate reads the hot wallet's on-chain balance; default it amply so the money-path
        // tests aren't gated, and let the funding-hold tests dial it down via SetHotFloat.
        _hotFloat = new InMemoryBalanceReader();
        _hotFloat.Set(Chain.Tron, HotWalletAddress, Asset, AmpleFloat);
        services.AddSingleton<IBalanceReader>(_hotFloat);

        // On-demand energy gate for the hot pool wallet — Ready by default so the money-path tests send; the
        // energy-gate test dials it to Provisioning.
        _energy = new StubEnergy();
        services.AddSingleton<IEnergyDelegationService>(_energy);

        _provider = services.BuildServiceProvider();

        await using var scope = _provider.CreateAsyncScope();
        var w = scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>();
        await w.Database.EnsureDeletedAsync(Ct);
        await w.Database.EnsureCreatedAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database
            .GetService<IRelationalDatabaseCreator>().CreateTablesAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await using (var scope = _provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>().Database.EnsureDeletedAsync(Ct);
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task The_full_money_out_path_debits_reserve_then_settles_custody_and_fee()
    {
        await SeedMerchantBalanceAsync(BigInteger.Parse("10000000")); // 10 USDT
        var amount = BigInteger.Parse("3000000"); // below approval threshold → auto-approved

        var request = await RequestAsync(amount, "idem-happy");
        request.IsSuccess.ShouldBeTrue();
        request.Value.Status.ShouldBe(nameof(WithdrawalStatus.Approved));

        // Merchant was debited amount+fee at reserve; held in clearing.
        (await BalanceAsync(AccountType.MerchantLiability, Merchant)).ShouldBe(BigInteger.Parse("6900000"));
        (await BalanceAsync(AccountType.WithdrawalClearing, null)).ShouldBe(BigInteger.Parse("3100000"));

        await ProcessAsync();  // build → sign → broadcast
        await ConfirmAsync();  // → WithdrawalConfirmed
        await DispatchAsync(); // → Ledger settle

        await using (var scope = _provider.CreateAsyncScope())
        {
            var w = await scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>().Withdrawals.SingleAsync(Ct);
            w.Status.ShouldBe(WithdrawalStatus.Confirmed);
            w.TransactionHash.ShouldNotBeNull();
        }

        (await BalanceAsync(AccountType.WithdrawalClearing, null)).ShouldBe(BigInteger.Zero);         // cleared
        (await BalanceAsync(AccountType.TreasuryAsset, null)).ShouldBe(BigInteger.Parse("7000000"));  // amount left custody
        (await BalanceAsync(AccountType.FeeRevenue, null)).ShouldBe(Fee);                             // fee kept as revenue
        (await BalanceAsync(AccountType.MerchantLiability, Merchant)).ShouldBe(BigInteger.Parse("6900000"));
    }

    [Fact]
    public async Task Without_energy_the_hot_wallet_does_not_sign_and_resumes_once_energy_is_ready()
    {
        await SeedMerchantBalanceAsync(BigInteger.Parse("10000000"));
        _energy.Readiness = EnergyReadiness.Provisioning; // gas hub still delegating energy to the hot wallet

        var request = await RequestAsync(BigInteger.Parse("3000000"), "idem-noenergy");
        request.Value.Status.ShouldBe(nameof(WithdrawalStatus.Approved));

        await ProcessAsync(); // energy not Ready ⇒ must not sign
        await using (var scope = _provider.CreateAsyncScope())
        {
            var w = await scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>().Withdrawals.SingleAsync(Ct);
            w.Status.ShouldBe(WithdrawalStatus.Approved);   // never advanced — no energy, no sign, no ~27 TRX burn
            w.HasSignedTransaction.ShouldBeFalse();
        }

        // Energy provisioned ⇒ the SAME withdrawal sends on the next pass.
        _energy.Readiness = EnergyReadiness.Ready;
        await ProcessAsync();
        await using (var scope = _provider.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>().Withdrawals.SingleAsync(Ct))
                .Status.ShouldBe(WithdrawalStatus.Broadcast);
    }

    [Fact]
    public async Task A_withdrawal_beyond_the_balance_is_refused_with_no_debit()
    {
        await SeedMerchantBalanceAsync(BigInteger.Parse("1000000"));

        var request = await RequestAsync(BigInteger.Parse("5000000"), "idem-broke");

        request.IsFailure.ShouldBeTrue();
        request.Error!.Code.ShouldBe(WithdrawalErrors.InsufficientBalance.Code);
        (await BalanceAsync(AccountType.MerchantLiability, Merchant)).ShouldBe(BigInteger.Parse("1000000")); // untouched
    }

    [Fact]
    public async Task Rejecting_an_approval_releases_the_reserved_funds()
    {
        await SeedMerchantBalanceAsync(BigInteger.Parse("10000000"));
        var amount = BigInteger.Parse("6000000"); // above threshold → PendingApproval

        var request = await RequestAsync(amount, "idem-reject");
        request.Value.Status.ShouldBe(nameof(WithdrawalStatus.PendingApproval));
        (await BalanceAsync(AccountType.MerchantLiability, Merchant)).ShouldBe(BigInteger.Parse("3900000")); // reserved

        // The Ops search screen must show this distinctly from "still processing" — it needs a human, the
        // worker can't move it forward on its own (§ OpsWithdrawalApprovalEndpoints).
        (await SearchStatusAsync(request.Value.WithdrawalId)).ShouldBe("pending_approval");

        await using (var scope = _provider.CreateAsyncScope())
        {
            var id = (await scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>().Withdrawals.SingleAsync(Ct)).Id;
            (await scope.ServiceProvider.GetRequiredService<IWithdrawalApprovalService>().RejectAsync(id, "ops", "manual", Ct))
                .IsSuccess.ShouldBeTrue();
        }

        await DispatchAsync(); // WithdrawalFailed → Ledger release

        (await BalanceAsync(AccountType.WithdrawalClearing, null)).ShouldBe(BigInteger.Zero);
        (await BalanceAsync(AccountType.MerchantLiability, Merchant)).ShouldBe(BigInteger.Parse("10000000")); // fully restored
    }

    [Fact]
    public async Task The_request_is_idempotent_on_the_client_key()
    {
        await SeedMerchantBalanceAsync(BigInteger.Parse("10000000"));

        var first = await RequestAsync(BigInteger.Parse("3000000"), "idem-dupe");
        var second = await RequestAsync(BigInteger.Parse("3000000"), "idem-dupe");

        first.Value.WithdrawalId.ShouldBe(second.Value.WithdrawalId); // same withdrawal
        await using var scope = _provider.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>().Withdrawals.CountAsync(Ct)).ShouldBe(1);
        (await BalanceAsync(AccountType.MerchantLiability, Merchant)).ShouldBe(BigInteger.Parse("6900000")); // debited once
    }

    // ── Level 1: fake-blockchain chain scenarios (revert / delay / reorg-after-broadcast) ──

    [Fact]
    public async Task A_transaction_that_reverts_on_chain_is_left_for_ops_never_settled()
    {
        // The most important previously-uncovered path: broadcast succeeds and the tx is mined, but it
        // REVERTS on-chain. Funds may not have moved as intended, so the withdrawal must stay Broadcast for
        // ops — never settled, and never released (releasing after broadcast could double-spend).
        await SeedMerchantBalanceAsync(BigInteger.Parse("10000000"));
        Engine.NextTransactionReverts = true;

        (await RequestAsync(BigInteger.Parse("3000000"), "idem-revert")).Value.Status.ShouldBe(nameof(WithdrawalStatus.Approved));
        await ProcessAsync();  // build → sign → broadcast (accepted, will report revert)
        await ConfirmAsync();  // status.Succeeded = false → left in Broadcast

        var w = await SingleWithdrawalAsync();
        w.Status.ShouldBe(WithdrawalStatus.Broadcast);       // not Confirmed, not Failed
        w.TransactionHash.ShouldNotBeNull();
        (await OutboxCountContainingAsync("WithdrawalConfirmed")).ShouldBe(0); // nothing settled

        // Funds are still held in clearing — neither settled to custody nor returned to the merchant.
        // (TreasuryAsset stays at the seed 10_000_000: no settle debited/credited it.)
        (await BalanceAsync(AccountType.WithdrawalClearing, null)).ShouldBe(BigInteger.Parse("3100000"));
        (await BalanceAsync(AccountType.TreasuryAsset, null)).ShouldBe(BigInteger.Parse("10000000"));
        (await BalanceAsync(AccountType.MerchantLiability, Merchant)).ShouldBe(BigInteger.Parse("6900000"));
    }

    [Fact]
    public async Task A_broadcast_the_node_rejects_fails_the_withdrawal_and_releases_the_funds()
    {
        // The other "failed transaction" flavour: the node refuses the broadcast, BEFORE anything reaches the
        // chain. Unlike a revert, this is safe to release — the merchant gets their funds back in full.
        await SeedMerchantBalanceAsync(BigInteger.Parse("10000000"));
        Engine.NextBroadcastSucceeds = false;

        await RequestAsync(BigInteger.Parse("3000000"), "idem-reject-broadcast");
        (await BalanceAsync(AccountType.MerchantLiability, Merchant)).ShouldBe(BigInteger.Parse("6900000")); // reserved

        await ProcessAsync();  // broadcast rejected → Fail → WithdrawalFailed
        await DispatchAsync(); // → Ledger release

        var w = await SingleWithdrawalAsync();
        w.Status.ShouldBe(WithdrawalStatus.Failed);
        w.TransactionHash.ShouldBeNull(); // never broadcast
        (await BalanceAsync(AccountType.WithdrawalClearing, null)).ShouldBe(BigInteger.Zero);
        (await BalanceAsync(AccountType.MerchantLiability, Merchant)).ShouldBe(BigInteger.Parse("10000000")); // fully restored
    }

    [Fact]
    public async Task A_transaction_not_yet_mined_is_polled_safely_and_settles_once_it_appears()
    {
        // Inclusion / network delay: the tx isn't visible for the first two confirmation passes. The tracker
        // must poll harmlessly (no settle, no state change) until it appears, then settle exactly once.
        await SeedMerchantBalanceAsync(BigInteger.Parse("10000000"));
        Engine.MineDelayPolls = 2;

        await RequestAsync(BigInteger.Parse("3000000"), "idem-delay");
        await ProcessAsync();

        await ConfirmAsync(); // poll 1 — not mined yet
        (await SingleWithdrawalAsync()).Status.ShouldBe(WithdrawalStatus.Broadcast);
        await ConfirmAsync(); // poll 2 — still not mined
        (await SingleWithdrawalAsync()).Status.ShouldBe(WithdrawalStatus.Broadcast);

        await ConfirmAsync(); // poll 3 — appears, buried to the confirmation depth → confirms
        await DispatchAsync();

        (await SingleWithdrawalAsync()).Status.ShouldBe(WithdrawalStatus.Confirmed);
        (await OutboxCountContainingAsync("WithdrawalConfirmed")).ShouldBe(1); // settled exactly once
        (await BalanceAsync(AccountType.WithdrawalClearing, null)).ShouldBe(BigInteger.Zero);
        // Custody drops by the amount that left the chain: seed 10_000_000 − 3_000_000 = 7_000_000.
        (await BalanceAsync(AccountType.TreasuryAsset, null)).ShouldBe(BigInteger.Parse("7000000"));
    }

    [Fact]
    public async Task A_broadcast_transaction_orphaned_by_a_reorg_is_not_falsely_settled()
    {
        // The tx was mined then dropped from the canonical chain (reorg after broadcast) before reaching the
        // confirmation depth. Status goes back to "not found", so the tracker must NOT settle — it keeps
        // waiting (a real re-broadcast / ops decision follows), never crediting custody on a vanished tx.
        await SeedMerchantBalanceAsync(BigInteger.Parse("10000000"));

        await RequestAsync(BigInteger.Parse("3000000"), "idem-reorg");
        await ProcessAsync();

        var txHash = (await SingleWithdrawalAsync()).TransactionHash!;
        Engine.OrphanTransaction(txHash); // reorg drops the mined tx

        await ConfirmAsync();

        var w = await SingleWithdrawalAsync();
        w.Status.ShouldBe(WithdrawalStatus.Broadcast); // still awaiting, not settled
        (await OutboxCountContainingAsync("WithdrawalConfirmed")).ShouldBe(0);
        // Custody untouched by a settle — still at the seed 10_000_000.
        (await BalanceAsync(AccountType.TreasuryAsset, null)).ShouldBe(BigInteger.Parse("10000000"));
    }

    // ── helpers ──

    private InMemoryTransactionEngine Engine => _provider.GetRequiredService<InMemoryTransactionEngine>();

    [Fact]
    public async Task An_underfunded_hot_wallet_parks_the_withdrawal_then_auto_resumes_when_reloaded()
    {
        await SeedMerchantBalanceAsync(BigInteger.Parse("10000000"));
        var amount = BigInteger.Parse("3000000"); // below threshold → auto-approved

        var request = await RequestAsync(amount, "idem-park");
        request.Value.Status.ShouldBe(nameof(WithdrawalStatus.Approved));

        // Hot wallet can't cover it → parked, reserve HELD (merchant stays debited), ops sees it distinctly.
        SetHotFloat(BigInteger.Parse("1000000")); // less than the 3,000,000 payout
        await ProcessAsync();

        var parked = await SingleWithdrawalAsync();
        parked.Status.ShouldBe(WithdrawalStatus.AwaitingFunds);
        parked.StatusReason.ShouldNotBeNull();
        (await SearchStatusAsync(parked.Id)).ShouldBe("insufficient_balance");
        (await BalanceAsync(AccountType.MerchantLiability, Merchant)).ShouldBe(BigInteger.Parse("6900000")); // reserved, NOT released
        (await BalanceAsync(AccountType.WithdrawalClearing, null)).ShouldBe(BigInteger.Parse("3100000"));

        // Admin reloads the hot wallet from treasury → the withdrawal auto-resumes and completes.
        SetHotFloat(AmpleFloat);
        await ProcessAsync();  // AwaitingFunds → build → sign → broadcast
        await ConfirmAsync();
        await DispatchAsync();

        (await SingleWithdrawalAsync()).Status.ShouldBe(WithdrawalStatus.Confirmed);
        (await BalanceAsync(AccountType.WithdrawalClearing, null)).ShouldBe(BigInteger.Zero);
        (await BalanceAsync(AccountType.TreasuryAsset, null)).ShouldBe(BigInteger.Parse("7000000")); // 3,000,000 left custody
        (await BalanceAsync(AccountType.FeeRevenue, null)).ShouldBe(Fee);
    }

    [Fact]
    public async Task An_above_threshold_withdrawal_is_held_for_operator_release_even_when_funded()
    {
        await SeedMerchantBalanceAsync(BigInteger.Parse("10000000"));
        var amount = BigInteger.Parse("6000000"); // above the 5,000,000 threshold → PendingApproval

        var request = await RequestAsync(amount, "idem-large");
        request.Value.Status.ShouldBe(nameof(WithdrawalStatus.PendingApproval));

        // Approve it (first human touch). The float is ample, but a large payout is held for an explicit
        // release before it sends — the "large = manual" resume rule.
        var w = await SingleWithdrawalAsync();
        await ApproveAsync(w.Id);
        await ProcessAsync();

        (await SingleWithdrawalAsync()).Status.ShouldBe(WithdrawalStatus.AwaitingRelease);
        (await SearchStatusAsync(w.Id)).ShouldBe("awaiting_release");

        // Operator releases → it sends and settles on the next pass.
        (await ReleaseAsync(w.Id)).IsSuccess.ShouldBeTrue();
        await ProcessAsync();
        await ConfirmAsync();
        await DispatchAsync();

        var settled = await SingleWithdrawalAsync();
        settled.Status.ShouldBe(WithdrawalStatus.Confirmed);
        settled.ReleasedBy.ShouldNotBeNull();
    }

    [Fact]
    public async Task One_wallet_processes_one_withdrawal_at_a_time_leased_until_confirmed()
    {
        await SeedMerchantBalanceAsync(BigInteger.Parse("20000000"));

        // A single-wallet pool (funded). Two payouts: the first leases the wallet; the second can't allocate it
        // (leased until the first confirms — one tx at a time per wallet), so it parks.
        (await RequestAsync(BigInteger.Parse("3000000"), "idem-a")).IsSuccess.ShouldBeTrue();
        (await RequestAsync(BigInteger.Parse("3000000"), "idem-b")).IsSuccess.ShouldBeTrue();

        await ProcessAsync(); // one broadcasts and leases the wallet; the other finds it busy → parked

        var all = await AllWithdrawalsAsync();
        var broadcast = all.Single(x => x.Status == WithdrawalStatus.Broadcast);
        broadcast.SourceWalletId.ShouldBe(HotWalletId); // stamped with the leased wallet
        all.Count(x => x.Status == WithdrawalStatus.AwaitingFunds).ShouldBe(1);

        // Confirm the first → the wallet is freed → the parked one allocates it and sends on the next pass.
        await ConfirmAsync();
        await DispatchAsync();
        await ProcessAsync();
        (await AllWithdrawalsAsync()).Count(x => x.Status is WithdrawalStatus.Broadcast or WithdrawalStatus.Confirmed).ShouldBe(2);
    }

    [Fact]
    public async Task Two_withdrawals_cannot_lease_the_same_wallet_the_second_save_reverts_cleanly()
    {
        await SeedMerchantBalanceAsync(BigInteger.Parse("20000000"));
        var a = (await RequestAsync(BigInteger.Parse("3000000"), "idem-x")).Value.WithdrawalId;
        var b = (await RequestAsync(BigInteger.Parse("3000000"), "idem-y")).Value.WithdrawalId;
        var walletW = Guid.CreateVersion7();

        // A leases wallet W (persisted → Signing).
        await using (var scope = _provider.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IWithdrawalRepository>();
            var wa = await repo.GetByIdAsync(a, Ct);
            wa!.RecordSigned(Guid.CreateVersion7(), walletW, [1], DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();
            (await repo.TrySaveSignedAsync(wa, Ct)).ShouldBeTrue();
        }

        // B tries the SAME wallet — the filtered unique index refuses it; TrySaveSignedAsync reverts cleanly.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IWithdrawalRepository>();
            var wb = await repo.GetByIdAsync(b, Ct);
            wb!.RecordSigned(Guid.CreateVersion7(), walletW, [2], DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();
            (await repo.TrySaveSignedAsync(wb, Ct)).ShouldBeFalse();
        }

        // B is untouched in the DB — still Approved, no wallet stamped — so it re-allocates another wallet next pass.
        await using (var verify = _provider.CreateAsyncScope())
        {
            var wb = await verify.ServiceProvider.GetRequiredService<WithdrawalDbContext>()
                .Withdrawals.SingleAsync(w => w.Id == b, Ct);
            wb.Status.ShouldBe(WithdrawalStatus.Approved);
            wb.SourceWalletId.ShouldBeNull();
        }
    }

    private async Task ApproveAsync(Guid id)
    {
        await using var scope = _provider.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<IWithdrawalApprovalService>().ApproveAsync(id, "ops", Ct))
            .IsSuccess.ShouldBeTrue();
    }

    private async Task<Result> ReleaseAsync(Guid id)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IWithdrawalFundingService>().ReleaseAsync(id, "ops", Ct);
    }

    private async Task<IReadOnlyList<WithdrawalEntity>> AllWithdrawalsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>().Withdrawals.ToListAsync(Ct);
    }

    private async Task<WithdrawalEntity> SingleWithdrawalAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>().Withdrawals.SingleAsync(Ct);
    }

    private async Task<int> OutboxCountContainingAsync(string typeFragment)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>()
            .OutboxMessages.CountAsync(m => m.Type.Contains(typeFragment), Ct);
    }

    private async Task SeedMerchantBalanceAsync(BigInteger amount)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILedgerPoster>()
            .CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, amount), Ct);
    }

    private async Task<Result<WithdrawalResult>> RequestAsync(BigInteger amount, string idempotencyKey)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IWithdrawalRequestService>()
            .RequestAsync(new RequestWithdrawalCommand(Merchant, Asset, Chain.Tron, "TDestination", amount, idempotencyKey), Ct);
    }

    private async Task<string?> SearchStatusAsync(Guid withdrawalId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var filter = new WithdrawalAdminFilter(Merchant, withdrawalId, null, null, null, null, null, null);
        var (items, _) = await scope.ServiceProvider.GetRequiredService<IWithdrawalDirectory>()
            .SearchAsync(filter, page: 1, pageSize: 10, Ct);
        return items.SingleOrDefault()?.Status;
    }

    private async Task ProcessAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<WithdrawalProcessingService>().ProcessOnceAsync(Ct);
    }

    private async Task ConfirmAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<WithdrawalConfirmationService>().TrackOnceAsync(Ct);
    }

    private async Task DispatchAsync()
    {
        var dispatcher = new OutboxDispatcher<WithdrawalDbContext>(
            _provider.GetRequiredService<IServiceScopeFactory>(), new NoOpLock(), TimeProvider.System,
            NullLogger<OutboxDispatcher<WithdrawalDbContext>>.Instance);
        await dispatcher.DispatchPendingAsync(Ct);
    }

    private async Task<BigInteger> BalanceAsync(AccountType type, Guid? ownerId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        return await ledger.AccountBalances
            .Join(ledger.Accounts, b => b.Id, a => a.Id, (b, a) => new { b, a })
            .Where(x => x.a.AccountType == type && x.a.OwnerId == ownerId && x.a.AssetId == Asset)
            .Select(x => x.b.Balance)
            .SingleOrDefaultAsync(Ct);
    }

    private sealed class StubPolicy : IWithdrawalPolicyProvider
    {
        public WithdrawalPolicy For(Chain chain) => Policy;
    }

    /// <summary>A stub energy gate. Ready by default (money-path tests send); a test dials Readiness to prove the
    /// withdrawal won't sign without energy, then back to Ready to prove it resumes.</summary>
    private sealed class StubEnergy : IEnergyDelegationService
    {
        public EnergyReadiness Readiness { get; set; } = EnergyReadiness.Ready;

        public Task<EnergyReadiness> EnsureEnergyForTransferAsync(Chain chain, string address, CancellationToken cancellationToken = default) =>
            Task.FromResult(Readiness);
    }

    private sealed class StubTreasuryPool(Guid walletId, string address) : ITreasuryHotWalletDirectory
    {
        private TreasuryHotWallet Wallet => new(walletId, Chain.Tron, address, "tron-hot-wallet-0");

        public Task<Result<TreasuryHotWallet>> GetHotWalletAsync(Chain chain, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(Wallet));

        public Task<IReadOnlyList<TreasuryHotWallet>> GetHotWalletPoolAsync(Chain chain, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TreasuryHotWallet>>([Wallet]);
    }

    private sealed class FakeMerchants : IMerchantDirectory
    {
        public Task<MerchantSummary?> FindByIdAsync(Guid merchantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MerchantSummary?>(new MerchantSummary(merchantId, "ACME", "Acme", null, CanTransact: true));

        public Task<MerchantSummary?> FindByCodeAsync(string merchantCode, CancellationToken cancellationToken = default) =>
            Task.FromResult<MerchantSummary?>(null);
    }

    /// <summary>Per-merchant pricing: a flat withdrawal fee, matching this suite's expectations.</summary>
    private sealed class FakeFees(BigInteger withdrawalFee) : IMerchantFeeSchedule
    {
        public Task<BigInteger> QuoteDepositFeeAsync(Guid merchantId, Guid assetId, BigInteger receivedAmount, CancellationToken cancellationToken = default) =>
            Task.FromResult(BigInteger.Zero);

        public Task<BigInteger> QuoteWithdrawalFeeAsync(Guid merchantId, Guid assetId, BigInteger amount, CancellationToken cancellationToken = default) =>
            Task.FromResult(withdrawalFee);

        public Task<Result<BigInteger>> GrossUpDepositAsync(Guid merchantId, Guid assetId, BigInteger netTarget, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(netTarget));
    }

    private sealed class StubChainStatus : IChainStatusReader
    {
        public Task<long> GetTipHeightAsync(Chain chain, CancellationToken cancellationToken = default) => Task.FromResult(1000L);
        public Task<BlockRef?> GetBlockAsync(Chain chain, long blockNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult<BlockRef?>(new BlockRef(blockNumber, "0xblock"));
        public Task<long> GetFinalizedHeightAsync(Chain chain, CancellationToken cancellationToken = default) => Task.FromResult(1000L);
    }

    private sealed class NoOpLock : IDistributedLockFactory
    {
        public Task<IAsyncDisposable> AcquireAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable>(new Handle());

        private sealed class Handle : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
