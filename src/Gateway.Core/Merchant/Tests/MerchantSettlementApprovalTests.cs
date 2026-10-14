using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Tests;

/// <summary>
/// Merchant-submitted cash-out addresses (2026-10-14). The load-bearing rule: a merchant can PROPOSE an address
/// but money only ever goes to one platform staff APPROVED — a stolen merchant login can never point earnings at a
/// new address on its own. Plus: one active per chain, the first approved becomes active automatically, and a
/// merchant may switch between approved addresses but never to a pending/rejected/sanctioned one.
/// </summary>
public sealed class MerchantSettlementApprovalTests : IAsyncLifetime
{
    private const string DbName = "CpeMerchantSettlementApprovalTests";
    private const string AddressA = "TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH";
    private const string AddressB = "TAo8wkS8n7bFofBesZk2FWipyuB9uZQJpy";
    private const string AddressC = "TQnfxcRoQLrDZVr33XBsvmmDJUHMDSjEsW";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static MerchantDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MerchantDbContext>().UseSqlServer(ConnectionString).UseBigIntegerMoney().Options);

    private static MerchantRegistrar Registrar(
        MerchantDbContext c, ScreeningDecision? screenAs = null) =>
        new(new MerchantRepository(c), new ApiCredentialGenerator(),
            new HmacApiSecretHasher(Options.Create(new ApiCredentialOptions
            {
                CurrentHashVersion = 1,
                Peppers = new Dictionary<int, string> { [1] = "test-pepper-value" },
            })),
            new AesGcmSecretCipher(Options.Create(new SigningSecretOptions
            {
                CurrentKeyVersion = 1,
                Keys = new Dictionary<int, string> { [1] = Convert.ToBase64String(new byte[32]) },
            })),
            TimeProvider.System,
            screenAs is { } d ? new Screening(d) : null,
            Options.Create(new MerchantScreeningOptions { ScreenSettlementWallets = screenAs is not null }));

    private sealed class Screening(ScreeningDecision decision) : IAddressScreeningService
    {
        public Task<ScreeningVerdict> ScreenAsync(Chain chain, string address, ScreeningPurpose purpose, CancellationToken ct = default) =>
            Task.FromResult(new ScreeningVerdict(Guid.CreateVersion7(), decision, 10, "Low", [], null, null, DateTimeOffset.UtcNow, false));
        public Task<ScreeningVerdict> ReScreenAsync(Chain chain, string address, ScreeningPurpose purpose, CancellationToken ct = default) =>
            ScreenAsync(chain, address, purpose, ct);
        public Task<ScreeningVerdict?> FindLatestAsync(Chain chain, string address, CancellationToken ct = default) =>
            Task.FromResult<ScreeningVerdict?>(null);
        public Task<IReadOnlyList<string>> FindAddressesNeedingScreeningAsync(
            Chain chain, IReadOnlyCollection<string> addresses, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    public async ValueTask InitializeAsync()
    {
        await using var context = NewContext();
        await context.Database.EnsureDeletedAsync(Ct);
        await context.Database.EnsureCreatedAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await using var context = NewContext();
        await context.Database.EnsureDeletedAsync(Ct);
    }

    private static async Task<Guid> NewMerchantAsync()
    {
        await using var c = NewContext();
        return (await Registrar(c).RegisterAsync("Settle Co " + Guid.NewGuid().ToString("N")[..6], null, Ct)).Value.MerchantId;
    }

    private static async Task<string?> PaidToAsync(Guid merchantId)
    {
        await using var c = NewContext();
        return await new MerchantSettlementDirectory(c).FindSettlementAddressAsync(merchantId, Chain.Tron, Ct);
    }

    private static async Task<Dictionary<string, MerchantSettlementWalletView>> WalletsAsync(Guid merchantId)
    {
        await using var c = NewContext();
        return (await Registrar(c).ListSettlementWalletsAsync(merchantId, Ct)).Value.ToDictionary(w => w.Address);
    }

    [Fact]
    public async Task A_merchant_submission_is_pending_and_never_paid_to_until_approved()
    {
        var m = await NewMerchantAsync();
        await using (var c = NewContext())
        {
            var sub = await Registrar(c).SubmitSettlementWalletAsync(m, Chain.Tron, AddressA, "my ledger", "merchant001", Ct);
            sub.IsSuccess.ShouldBeTrue();
            sub.Value.Status.ShouldBe("PendingApproval");

            // The merchant can't make its own unapproved address the destination.
            (await Registrar(c).ActivateSettlementWalletAsync(m, sub.Value.WalletId, Ct, screenNow: false))
                .Error!.Code.ShouldBe(MerchantErrors.SettlementWalletNotApproved.Code);
        }

        (await PaidToAsync(m)).ShouldBeNull(); // cash-out would still be refused
        var w = (await WalletsAsync(m))[AddressA];
        w.Source.ShouldBe("Merchant");
        w.SubmittedBy.ShouldBe("merchant001");
    }

    [Fact]
    public async Task The_first_approved_address_becomes_active_automatically()
    {
        var m = await NewMerchantAsync();
        Guid id;
        await using (var c = NewContext())
            id = (await Registrar(c).SubmitSettlementWalletAsync(m, Chain.Tron, AddressA, null, "merchant001", Ct)).Value.WalletId;

        await using (var c = NewContext())
        {
            var approved = await Registrar(c, ScreeningDecision.Allow).ApproveSettlementWalletAsync(m, id, "staff.alice", Ct);
            approved.IsSuccess.ShouldBeTrue();
        }

        (await PaidToAsync(m)).ShouldBe(AddressA);
        var w = (await WalletsAsync(m))[AddressA];
        w.Status.ShouldBe("Active");
        w.ReviewedBy.ShouldBe("staff.alice");
        w.ScreeningDecision.ShouldBe("Allow");
    }

    /// <summary>With an active address already in place, approving another puts it on standby — the money keeps
    /// going where it went — and the merchant may then switch to it; the old one goes back to standby.</summary>
    [Fact]
    public async Task Approving_a_second_address_keeps_the_current_one_and_the_merchant_can_switch()
    {
        var m = await NewMerchantAsync();
        await using (var c = NewContext())
            (await Registrar(c).AddSettlementWalletAsync(m, Chain.Tron, AddressA, null, activate: true, Ct, addedBy: "staff.bob"))
                .IsSuccess.ShouldBeTrue();

        Guid second;
        await using (var c = NewContext())
            second = (await Registrar(c).SubmitSettlementWalletAsync(m, Chain.Tron, AddressB, null, "merchant001", Ct)).Value.WalletId;
        await using (var c = NewContext())
            (await Registrar(c).ApproveSettlementWalletAsync(m, second, "staff.alice", Ct)).Value.Status.ShouldBe("Retired");

        (await PaidToAsync(m)).ShouldBe(AddressA); // unchanged

        await using (var c = NewContext())
            (await Registrar(c).ActivateSettlementWalletAsync(m, second, Ct, screenNow: false)).IsSuccess.ShouldBeTrue();

        (await PaidToAsync(m)).ShouldBe(AddressB);
        var all = await WalletsAsync(m);
        all[AddressA].Status.ShouldBe("Retired");
        all[AddressB].Status.ShouldBe("Active");
        all.Values.Count(w => w.Status == "Active").ShouldBe(1);
    }

    [Fact]
    public async Task A_rejection_needs_a_reason_and_the_merchant_can_submit_again()
    {
        var m = await NewMerchantAsync();
        Guid id;
        await using (var c = NewContext())
            id = (await Registrar(c).SubmitSettlementWalletAsync(m, Chain.Tron, AddressA, null, "merchant001", Ct)).Value.WalletId;

        await using (var c = NewContext())
        {
            (await Registrar(c).RejectSettlementWalletAsync(m, id, "staff.alice", "  ", Ct))
                .Error!.Code.ShouldBe(MerchantErrors.SettlementWalletRejectionReasonRequired.Code);
            (await Registrar(c).RejectSettlementWalletAsync(m, id, "staff.alice", "Not your company wallet", Ct))
                .Value.Status.ShouldBe("Rejected");
            (await Registrar(c).ActivateSettlementWalletAsync(m, id, Ct, screenNow: false))
                .Error!.Code.ShouldBe(MerchantErrors.SettlementWalletNotApproved.Code);
        }

        (await WalletsAsync(m))[AddressA].RejectionReason.ShouldBe("Not your company wallet");

        await using (var c = NewContext())
        {
            var again = await Registrar(c).SubmitSettlementWalletAsync(m, Chain.Tron, AddressA, null, "merchant001", Ct);
            again.Value.Status.ShouldBe("PendingApproval");
            again.Value.WalletId.ShouldBe(id); // same record, re-opened — not a duplicate
        }
        (await WalletsAsync(m))[AddressA].RejectionReason.ShouldBeNull();
    }

    [Fact]
    public async Task A_merchant_can_cancel_its_pending_request_and_a_cancelled_one_cannot_be_approved()
    {
        var m = await NewMerchantAsync();
        Guid id;
        await using (var c = NewContext())
            id = (await Registrar(c).SubmitSettlementWalletAsync(m, Chain.Tron, AddressA, null, "merchant001", Ct)).Value.WalletId;

        await using (var c = NewContext())
        {
            (await Registrar(c).CancelSettlementWalletAsync(m, id, Ct)).Value.Status.ShouldBe("Cancelled");
            (await Registrar(c).ApproveSettlementWalletAsync(m, id, "staff.alice", Ct))
                .Error!.Code.ShouldBe(MerchantErrors.SettlementWalletNotPending.Code);
        }
        (await PaidToAsync(m)).ShouldBeNull();
    }

    [Fact]
    public async Task Submitting_an_address_already_approved_or_pending_is_refused()
    {
        var m = await NewMerchantAsync();
        await using (var c = NewContext())
        {
            (await Registrar(c).AddSettlementWalletAsync(m, Chain.Tron, AddressA, null, activate: true, Ct, addedBy: "staff.bob"))
                .IsSuccess.ShouldBeTrue();
            (await Registrar(c).SubmitSettlementWalletAsync(m, Chain.Tron, AddressA, null, "merchant001", Ct))
                .Error!.Code.ShouldBe(MerchantErrors.SettlementWalletAlreadyOnFile.Code);

            (await Registrar(c).SubmitSettlementWalletAsync(m, Chain.Tron, AddressB, null, "merchant001", Ct)).IsSuccess.ShouldBeTrue();
            (await Registrar(c).SubmitSettlementWalletAsync(m, Chain.Tron, AddressB, null, "merchant001", Ct))
                .Error!.Code.ShouldBe(MerchantErrors.SettlementWalletAlreadyOnFile.Code);
        }
    }

    /// <summary>Staff adding an address the merchant had submitted is itself the approval.</summary>
    [Fact]
    public async Task Staff_adding_a_pending_address_approves_it()
    {
        var m = await NewMerchantAsync();
        await using (var c = NewContext())
            (await Registrar(c).SubmitSettlementWalletAsync(m, Chain.Tron, AddressA, null, "merchant001", Ct)).IsSuccess.ShouldBeTrue();

        await using (var c = NewContext())
            (await Registrar(c).AddSettlementWalletAsync(m, Chain.Tron, AddressA, null, activate: false, Ct, addedBy: "staff.bob"))
                .IsSuccess.ShouldBeTrue();

        (await PaidToAsync(m)).ShouldBe(AddressA); // first approved → active automatically
        (await WalletsAsync(m))[AddressA].ReviewedBy.ShouldBe("staff.bob");
    }

    /// <summary>A sanctioned address can't be approved (it stays pending, the verdict recorded), and the merchant BO —
    /// which has no screening module — can't make one active either: it uses the decision stored on the wallet.</summary>
    [Fact]
    public async Task A_sanctioned_address_is_never_approved_or_activated()
    {
        var m = await NewMerchantAsync();
        Guid pending;
        await using (var c = NewContext())
            pending = (await Registrar(c).SubmitSettlementWalletAsync(m, Chain.Tron, AddressA, null, "merchant001", Ct)).Value.WalletId;

        await using (var c = NewContext())
            (await Registrar(c, ScreeningDecision.Block).ApproveSettlementWalletAsync(m, pending, "staff.alice", Ct))
                .Error!.Code.ShouldBe(MerchantErrors.SettlementWalletBlocked.Code);

        var a = (await WalletsAsync(m))[AddressA];
        a.Status.ShouldBe("PendingApproval");
        a.ScreeningDecision.ShouldBe("Block");

        // Staff keep a sanctioned address on file (whitelisting is never refused) — it is NOT auto-activated…
        Guid blocked;
        await using (var c = NewContext())
            blocked = (await Registrar(c, ScreeningDecision.Block)
                .AddSettlementWalletAsync(m, Chain.Tron, AddressC, null, activate: false, Ct, addedBy: "staff.bob")).Value.WalletId;
        (await PaidToAsync(m)).ShouldBeNull();

        // …and the merchant can't switch to it, even without a screening module in its host.
        await using (var c = NewContext())
            (await Registrar(c).ActivateSettlementWalletAsync(m, blocked, Ct, screenNow: false))
                .Error!.Code.ShouldBe(MerchantErrors.SettlementWalletBlocked.Code);
    }

    [Fact]
    public async Task The_admin_queue_lists_pending_requests_across_merchants()
    {
        var m1 = await NewMerchantAsync();
        var m2 = await NewMerchantAsync();
        await using (var c = NewContext())
        {
            await Registrar(c).SubmitSettlementWalletAsync(m1, Chain.Tron, AddressA, null, "m1.user", Ct);
            await Registrar(c).SubmitSettlementWalletAsync(m2, Chain.Tron, AddressB, null, "m2.user", Ct);
            await Registrar(c).AddSettlementWalletAsync(m2, Chain.Tron, AddressC, null, activate: true, Ct, addedBy: "staff.bob");
        }

        await using var read = NewContext();
        var (items, _) = await Registrar(read).ListSettlementWalletQueueAsync(SettlementWalletStatus.PendingApproval, null, 1, 200, Ct);
        var mine = items.Where(i => i.MerchantId == m1 || i.MerchantId == m2).ToList();
        mine.Count.ShouldBe(2);
        mine.ShouldAllBe(i => i.Wallet.Status == "PendingApproval");
        mine.Single(i => i.MerchantId == m1).MerchantCode.ShouldNotBeNullOrWhiteSpace();
    }
}
