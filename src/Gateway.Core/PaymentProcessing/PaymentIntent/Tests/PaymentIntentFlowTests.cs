using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Events;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application.Handlers;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Tests;

/// <summary>
/// The invoice flow against real SQL Server: address reservation and reuse (the concentration strategy),
/// idempotency, matching a confirmed deposit, and the redelivery guard that stops an old deposit from
/// hijacking a newer invoice on a reused address. Wallet's provisioning Contract is substituted (each call
/// mints a distinct address), so this stays focused on PaymentIntent + its DB indexes.
/// </summary>
public sealed class PaymentIntentFlowTests : IAsyncLifetime
{
    private const string DbName = "CpePaymentIntentTests";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly BigInteger OneUsdt = BigInteger.Parse("1000000");

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static PaymentIntentDbContext Context() =>
        new(new DbContextOptionsBuilder<PaymentIntentDbContext>().UseSqlServer(ConnectionString).UseBigIntegerMoney().Options);

    private sealed class MintCounter { public int Value; }

    /// <summary>
    /// Stands in for the Wallet module's real directory. <see cref="PaymentIntentService"/> now sources
    /// reuse candidates from here directly (so a pre-provisioned pool is visible before any invoice ever
    /// touches a wallet) — this fake mirrors that by recording whatever the fake provisioner "mints" below.
    /// </summary>
    private sealed class FakeWalletDirectory : IWalletDirectory
    {
        private readonly List<AvailableWallet> _wallets = [];

        public void Register(Guid walletId, string address) => _wallets.Add(new AvailableWallet(walletId, address));

        public Task<WalletOwnership?> FindByAddressAsync(Chain chain, string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<WalletOwnership?>(null);

        public Task<WalletOwnership?> FindByIdAsync(Guid walletId, CancellationToken cancellationToken = default) =>
            Task.FromResult<WalletOwnership?>(null);

        public Task<IReadOnlyList<AvailableWallet>> ListAssignedWalletsAsync(
            Guid merchantId, Chain chain, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AvailableWallet>>(_wallets.AsReadOnly());
    }

    /// <summary>
    /// In-memory stand-in for the Redis-backed reservation — real claim/release semantics (a walletId can
    /// only be held once at a time), no TTL expiry (tests that need "already reserved" pre-seed a claim
    /// directly via <see cref="ForceReserve"/> rather than waiting out a clock).
    /// </summary>
    private sealed class FakeWalletReservationLock : IWalletReservationLock
    {
        private readonly HashSet<Guid> _held = [];

        public void ForceReserve(Guid walletId) => _held.Add(walletId);

        public Task<bool> TryReserveAsync(Guid walletId, string referenceId, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            Task.FromResult(_held.Add(walletId));

        public Task ReleaseAsync(Guid walletId, CancellationToken cancellationToken = default)
        {
            _held.Remove(walletId);
            return Task.CompletedTask;
        }
    }

    private static IDepositAddressProvisioner Provisioner(MintCounter counter, FakeWalletDirectory directory)
    {
        var provisioner = Substitute.For<IDepositAddressProvisioner>();
        provisioner.ProvisionDepositAddressAsync(Arg.Any<Guid>(), Arg.Any<Chain>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                counter.Value++;
                var walletId = Guid.CreateVersion7();
                var address = "T" + Guid.NewGuid().ToString("N")[..19];
                directory.Register(walletId, address);
                return Result.Success(new ProvisionedDepositAddress(walletId, Chain.Tron, address));
            });
        return provisioner;
    }

    /// <summary>An unpriced merchant: the invoice is not grossed up and no fee is taken (identity).</summary>
    private static IMerchantFeeSchedule NoFees()
    {
        var fees = Substitute.For<IMerchantFeeSchedule>();
        fees.GrossUpDepositAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<BigInteger>(), Arg.Any<CancellationToken>())
            .Returns(ci => Result.Success((BigInteger)ci[2]));
        fees.QuoteDepositFeeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<BigInteger>(), Arg.Any<CancellationToken>())
            .Returns(BigInteger.Zero);
        return fees;
    }

    private static PaymentIntentService Service(
        PaymentIntentDbContext context, IDepositAddressProvisioner provisioner, IWalletDirectory directory, IWalletReservationLock? walletLock = null) =>
        new(new PaymentIntentRepository(context), directory, walletLock ?? new FakeWalletReservationLock(), provisioner, NoFees(),
            Options.Create(new PaymentIntentOptions { ExpiryMinutes = 30 }),
            TimeProvider.System, NullLogger<PaymentIntentService>.Instance);

    private static PaymentIntentMatchHandler Handler(PaymentIntentDbContext context, IWalletReservationLock? walletLock = null) =>
        new(new PaymentIntentRepository(context), walletLock ?? new FakeWalletReservationLock(),
            TimeProvider.System, NullLogger<PaymentIntentMatchHandler>.Instance);

    private static PaymentIntentAdminService AdminService(PaymentIntentDbContext context, IWalletReservationLock walletLock) =>
        new(new PaymentIntentRepository(context), walletLock, TimeProvider.System, NullLogger<PaymentIntentAdminService>.Instance);

    private static async Task<Guid> WalletOfAsync(Guid reference)
    {
        await using var context = Context();
        return (await context.PaymentIntents.AsNoTracking().SingleAsync(i => i.PublicReference == reference, Ct)).WalletId;
    }

    public async ValueTask InitializeAsync()
    {
        await using var context = Context();
        await context.Database.EnsureDeletedAsync(Ct);
        await context.Database.EnsureCreatedAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await using var context = Context();
        await context.Database.EnsureDeletedAsync(Ct);
    }

    [Fact]
    public async Task Creating_an_intent_reserves_an_address_and_persists_it_waiting()
    {
        var counter = new MintCounter();
        var directory = new FakeWalletDirectory();
        await using var context = Context();

        var result = await Service(context, Provisioner(counter, directory), directory).CreateAsync(
            new CreatePaymentIntentCommand(Merchant, "tx-1", Chain.Tron, Asset, OneUsdt, "https://m.test/cb"), Ct);

        result.IsSuccess.ShouldBeTrue();
        counter.Value.ShouldBe(1);

        await using var verify = Context();
        var intent = await verify.PaymentIntents.SingleAsync(Ct);
        intent.Status.ShouldBe(PaymentIntentStatus.Waiting);
        intent.PublicReference.ShouldBe(result.Value.Reference);
        intent.Address.ShouldBe(result.Value.Address);
    }

    [Fact]
    public async Task The_same_merchant_reference_is_idempotent()
    {
        var counter = new MintCounter();
        var directory = new FakeWalletDirectory();
        await using var context = Context();
        var service = Service(context, Provisioner(counter, directory), directory);

        var first = await service.CreateAsync(new CreatePaymentIntentCommand(Merchant, "tx-dup", Chain.Tron, Asset, OneUsdt, null), Ct);
        var second = await service.CreateAsync(new CreatePaymentIntentCommand(Merchant, "tx-dup", Chain.Tron, Asset, OneUsdt, null), Ct);

        second.Value.Reference.ShouldBe(first.Value.Reference);
        counter.Value.ShouldBe(1); // the replay minted nothing

        await using var verify = Context();
        (await verify.PaymentIntents.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_freed_address_is_reused_by_the_next_invoice()
    {
        var counter = new MintCounter();
        var directory = new FakeWalletDirectory();
        var provisioner = Provisioner(counter, directory);

        var a = await CreateAsync(provisioner, directory, "tx-a");
        var walletA = await WalletOfAsync(a);

        // Match A → its address is free again.
        await using (var context = Context())
        {
            var intent = await new PaymentIntentRepository(context).FindWaitingByWalletAsync(walletA, Ct);
            intent!.MatchTo(Guid.CreateVersion7(), "0xtx", OneUsdt, DateTimeOffset.UtcNow);
            await context.SaveChangesAsync(Ct);
        }

        await CreateAsync(provisioner, directory, "tx-b");

        counter.Value.ShouldBe(1); // B reused A's address; nothing new minted
        await using var verify = Context();
        (await verify.PaymentIntents.SingleAsync(i => i.MerchantTransactionId == "tx-b", Ct)).WalletId.ShouldBe(walletA);
    }

    [Fact]
    public async Task A_confirmed_deposit_matches_the_waiting_invoice()
    {
        var counter = new MintCounter();
        var directory = new FakeWalletDirectory();
        var reference = await CreateAsync(Provisioner(counter, directory), directory, "tx-m");
        var walletId = await WalletOfAsync(reference);

        await using (var context = Context())
            await Handler(context).HandleAsync(DepositTo(walletId, "1000000"), Ct);

        await using var verify = Context();
        var intent = await verify.PaymentIntents.SingleAsync(i => i.PublicReference == reference, Ct);
        intent.Status.ShouldBe(PaymentIntentStatus.Matched);
        intent.AmountMatched.ShouldBe(true);
    }

    [Fact]
    public async Task A_redelivered_old_deposit_cannot_hijack_a_newer_invoice_on_the_reused_address()
    {
        var counter = new MintCounter();
        var directory = new FakeWalletDirectory();
        var provisioner = Provisioner(counter, directory);

        var a = await CreateAsync(provisioner, directory, "tx-a2");
        var walletId = await WalletOfAsync(a);
        var deposit = DepositTo(walletId, "1000000");

        await using (var context = Context()) await Handler(context).HandleAsync(deposit, Ct); // matches A, frees address
        await CreateAsync(provisioner, directory, "tx-b2");                                                // B reuses the address
        await using (var context = Context()) await Handler(context).HandleAsync(deposit, Ct); // redeliver the OLD deposit

        counter.Value.ShouldBe(1); // B reused A's address
        await using var verify = Context();
        var b = await verify.PaymentIntents.SingleAsync(i => i.MerchantTransactionId == "tx-b2", Ct);
        b.WalletId.ShouldBe(walletId);
        b.Status.ShouldBe(PaymentIntentStatus.Waiting); // untouched by the redelivered old deposit
    }

    [Fact]
    public async Task Expiry_frees_a_lapsed_invoices_address_for_reuse()
    {
        var counter = new MintCounter();
        var directory = new FakeWalletDirectory();
        var provisioner = Provisioner(counter, directory);

        var reference = await CreateAsync(provisioner, directory, "tx-exp");
        var walletId = await WalletOfAsync(reference);

        await using (var context = Context())
            (await new PaymentIntentRepository(context).ExpireStaleAsync(DateTimeOffset.UtcNow.AddHours(1), 100, Ct)).ShouldBe(1);

        await CreateAsync(provisioner, directory, "tx-after-exp");

        counter.Value.ShouldBe(1); // reused the expired invoice's address
        await using var verify = Context();
        (await verify.PaymentIntents.SingleAsync(i => i.MerchantTransactionId == "tx-after-exp", Ct)).WalletId.ShouldBe(walletId);
    }

    private static async Task<Guid> CreateAsync(
        IDepositAddressProvisioner provisioner, FakeWalletDirectory directory, string reference, IWalletReservationLock? walletLock = null)
    {
        await using var context = Context();
        var result = await Service(context, provisioner, directory, walletLock).CreateAsync(
            new CreatePaymentIntentCommand(Merchant, reference, Chain.Tron, Asset, OneUsdt, null), Ct);
        result.IsSuccess.ShouldBeTrue();
        return result.Value.Reference;
    }

    private static DepositConfirmed DepositTo(Guid walletId, string amountBaseUnits) =>
        new(Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.CreateVersion7(), walletId, Merchant, Asset,
            amountBaseUnits, Chain.Tron, "0x" + Guid.NewGuid().ToString("N"), 0, DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_reserved_busiest_wallet_is_skipped_in_favour_of_the_next_free_one()
    {
        // Two pre-provisioned wallets; the first (busiest, listed first) is already held by another
        // in-flight invoice's reservation. Creation must walk past it to the second rather than minting new.
        var counter = new MintCounter();
        var directory = new FakeWalletDirectory();
        var walletBusy = Guid.CreateVersion7();
        directory.Register(walletBusy, "TWalletBusy");
        directory.Register(Guid.CreateVersion7(), "TWalletFree");

        var sharedLock = new FakeWalletReservationLock();
        sharedLock.ForceReserve(walletBusy); // simulates another in-flight invoice already holding it

        await using var context = Context();
        var provisioner = Provisioner(counter, directory);

        var result = await Service(context, provisioner, directory, sharedLock).CreateAsync(
            new CreatePaymentIntentCommand(Merchant, "tx-contended", Chain.Tron, Asset, OneUsdt, null), Ct);

        result.IsSuccess.ShouldBeTrue();
        counter.Value.ShouldBe(0); // reused an existing wallet — never minted
        result.Value.Address.ShouldBe("TWalletFree");
    }

    [Fact]
    public async Task A_confirmed_match_releases_the_wallet_reservation()
    {
        var counter = new MintCounter();
        var directory = new FakeWalletDirectory();
        var sharedLock = new FakeWalletReservationLock();
        var provisioner = Provisioner(counter, directory);

        var reference = await CreateAsync(provisioner, directory, "tx-release-match", sharedLock);
        var walletId = await WalletOfAsync(reference);

        await using (var context = Context())
            await Handler(context, sharedLock).HandleAsync(DepositTo(walletId, "1000000"), Ct);

        (await sharedLock.TryReserveAsync(walletId, "someone-else", TimeSpan.FromMinutes(1), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Manually_failing_a_waiting_intent_releases_its_wallet_and_marks_it_failed()
    {
        var counter = new MintCounter();
        var directory = new FakeWalletDirectory();
        var sharedLock = new FakeWalletReservationLock();
        var provisioner = Provisioner(counter, directory);

        var reference = await CreateAsync(provisioner, directory, "tx-fail", sharedLock);
        var walletId = await WalletOfAsync(reference);

        await using (var context = Context())
        {
            var result = await AdminService(context, sharedLock).FailAsync(
                new FailPaymentIntentCommand(reference, "merchant testing"), Ct);
            result.IsSuccess.ShouldBeTrue();
        }

        await using var verify = Context();
        (await verify.PaymentIntents.SingleAsync(i => i.PublicReference == reference, Ct)).Status.ShouldBe(PaymentIntentStatus.Failed);
        (await sharedLock.TryReserveAsync(walletId, "someone-else", TimeSpan.FromMinutes(1), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Failing_an_already_matched_intent_returns_a_conflict_and_leaves_the_reservation_alone()
    {
        var counter = new MintCounter();
        var directory = new FakeWalletDirectory();
        var sharedLock = new FakeWalletReservationLock();
        var provisioner = Provisioner(counter, directory);

        var reference = await CreateAsync(provisioner, directory, "tx-fail-too-late", sharedLock);
        var walletId = await WalletOfAsync(reference);

        await using (var context = Context())
            await Handler(context, sharedLock).HandleAsync(DepositTo(walletId, "1000000"), Ct); // matches + releases

        await using var admin = Context();
        var result = await AdminService(admin, sharedLock).FailAsync(new FailPaymentIntentCommand(reference, "too late"), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(PaymentIntentErrors.InvalidStateTransition.Code);
    }
}
