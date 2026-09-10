using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Audit.Tests;

public sealed class AuditServiceTests : IAsyncLifetime
{
    private const string DbName = "CpeAuditTests";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static AuditDbContext Context() =>
        new(new DbContextOptionsBuilder<AuditDbContext>().UseSqlServer(ConnectionString).Options);

    private static AuditService Service(AuditDbContext context) => new(new AuditEntryRepository(context), TimeProvider.System);

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
    public async Task Logging_an_entry_makes_it_searchable()
    {
        var staffId = Guid.CreateVersion7();

        await using var write = Context();
        await Service(write).LogAsync(new LogAuditEntryCommand(
            staffId, "admin", "merchant.created", "Merchant", "merchant-123", "code=ACME", "127.0.0.1"), Ct);

        await using var read = Context();
        var (items, total) = await Service(read).SearchAsync(new AuditScope.Platform(), new AuditSearchFilter(null, null, null, null, null, null), 1, 50, Ct);

        total.ShouldBe(1);
        items[0].StaffUsername.ShouldBe("admin");
        items[0].Action.ShouldBe("merchant.created");
        items[0].EntityId.ShouldBe("merchant-123");
        items[0].Reason.ShouldBe("code=ACME");
    }

    [Fact]
    public async Task Search_filters_by_staff_user()
    {
        var alice = Guid.CreateVersion7();
        var bob = Guid.CreateVersion7();

        await using var write = Context();
        var service = Service(write);
        await service.LogAsync(new LogAuditEntryCommand(alice, "alice", "role.created", "Role", "r1", null, null), Ct);
        await service.LogAsync(new LogAuditEntryCommand(bob, "bob", "role.created", "Role", "r2", null, null), Ct);

        await using var read = Context();
        var (items, total) = await Service(read).SearchAsync(new AuditScope.Platform(), new AuditSearchFilter(alice, null, null, null, null, null), 1, 50, Ct);

        total.ShouldBe(1);
        items[0].StaffUsername.ShouldBe("alice");
    }

    [Fact]
    public async Task Search_filters_by_action_and_entity_type()
    {
        var staffId = Guid.CreateVersion7();

        await using var write = Context();
        var service = Service(write);
        await service.LogAsync(new LogAuditEntryCommand(staffId, "admin", "withdrawal.approved", "Withdrawal", "w1", null, null), Ct);
        await service.LogAsync(new LogAuditEntryCommand(staffId, "admin", "withdrawal.rejected", "Withdrawal", "w2", "bad", null), Ct);
        await service.LogAsync(new LogAuditEntryCommand(staffId, "admin", "merchant.created", "Merchant", "m1", null, null), Ct);

        await using var read = Context();
        var (items, total) = await Service(read).SearchAsync(
            new AuditScope.Platform(),
            new AuditSearchFilter(null, "withdrawal.approved", "Withdrawal", null, null, null), 1, 50, Ct);

        total.ShouldBe(1);
        items[0].EntityId.ShouldBe("w1");
    }

    [Fact]
    public async Task Results_come_back_newest_first()
    {
        var staffId = Guid.CreateVersion7();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await using var first = Context();
        await new AuditService(new AuditEntryRepository(first), clock).LogAsync(
            new LogAuditEntryCommand(staffId, "admin", "action.one", "Entity", "1", null, null), Ct);

        clock.Advance(TimeSpan.FromMinutes(1));
        await using var second = Context();
        await new AuditService(new AuditEntryRepository(second), clock).LogAsync(
            new LogAuditEntryCommand(staffId, "admin", "action.two", "Entity", "2", null, null), Ct);

        await using var read = Context();
        var (items, _) = await Service(read).SearchAsync(new AuditScope.Platform(), new AuditSearchFilter(null, null, null, null, null, null), 1, 50, Ct);

        items[0].EntityId.ShouldBe("2");
        items[1].EntityId.ShouldBe("1");
    }

    // ── tenant isolation: the merchant activity log must never leak across tenants ──

    private static AuditSearchFilter Empty => new(null, null, null, null, null, null);

    /// <summary>
    /// The load-bearing test for the merchant-facing activity log. Three entries exist — one for merchant A,
    /// one for merchant B, and one platform-staff entry with no tenant — and merchant A must see exactly its
    /// own. A regression here would expose one merchant's administrative history (account names, when security
    /// controls were changed) to another, or leak staff activity into a customer-facing screen.
    /// </summary>
    [Fact]
    public async Task A_merchant_scope_sees_only_its_own_entries_never_another_tenants_nor_staff()
    {
        var merchantA = Guid.CreateVersion7();
        var merchantB = Guid.CreateVersion7();

        await using var write = Context();
        var service = Service(write);
        await service.LogAsync(new LogAuditEntryCommand(
            Guid.CreateVersion7(), "alice", "portal.role.created", "MerchantRole", "role-a", null, null, merchantA), Ct);
        await service.LogAsync(new LogAuditEntryCommand(
            Guid.CreateVersion7(), "bob", "portal.role.created", "MerchantRole", "role-b", null, null, merchantB), Ct);
        await service.LogAsync(new LogAuditEntryCommand(
            Guid.CreateVersion7(), "staff", "merchant.fee_updated", "Merchant", "m-1", null, null), Ct);

        await using var read = Context();
        var (items, total) = await Service(read).SearchAsync(new AuditScope.Merchant(merchantA), Empty, 1, 50, Ct);

        total.ShouldBe(1);
        items[0].EntityId.ShouldBe("role-a");
        items[0].MerchantId.ShouldBe(merchantA);
    }

    /// <summary>Platform staff see everything — both merchants' portal actions and staff's own.</summary>
    [Fact]
    public async Task A_platform_scope_sees_staff_and_every_merchants_entries()
    {
        var merchantA = Guid.CreateVersion7();

        await using var write = Context();
        var service = Service(write);
        await service.LogAsync(new LogAuditEntryCommand(
            Guid.CreateVersion7(), "alice", "portal.role.created", "MerchantRole", "role-a", null, null, merchantA), Ct);
        await service.LogAsync(new LogAuditEntryCommand(
            Guid.CreateVersion7(), "staff", "merchant.fee_updated", "Merchant", "m-1", null, null), Ct);

        await using var read = Context();
        var (_, total) = await Service(read).SearchAsync(new AuditScope.Platform(), Empty, 1, 50, Ct);

        total.ShouldBe(2);
    }

    /// <summary>The optional search filters can never widen a merchant scope: asking for the exact entity id
    /// of another tenant's row still returns nothing.</summary>
    [Fact]
    public async Task Filters_cannot_widen_a_merchant_scope_to_another_tenants_row()
    {
        var merchantA = Guid.CreateVersion7();
        var merchantB = Guid.CreateVersion7();

        await using var write = Context();
        await Service(write).LogAsync(new LogAuditEntryCommand(
            Guid.CreateVersion7(), "bob", "portal.role.deleted", "MerchantRole", "role-b", null, null, merchantB), Ct);

        await using var read = Context();
        var (items, total) = await Service(read).SearchAsync(
            new AuditScope.Merchant(merchantA),
            new AuditSearchFilter(null, "portal.role.deleted", "MerchantRole", "role-b", null, null), 1, 50, Ct);

        total.ShouldBe(0);
        items.ShouldBeEmpty();
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
