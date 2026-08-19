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
        var (items, total) = await Service(read).SearchAsync(new AuditSearchFilter(null, null, null, null, null, null), 1, 50, Ct);

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
        var (items, total) = await Service(read).SearchAsync(new AuditSearchFilter(alice, null, null, null, null, null), 1, 50, Ct);

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
        var (items, _) = await Service(read).SearchAsync(new AuditSearchFilter(null, null, null, null, null, null), 1, 50, Ct);

        items[0].EntityId.ShouldBe("2");
        items[1].EntityId.ShouldBe("1");
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
