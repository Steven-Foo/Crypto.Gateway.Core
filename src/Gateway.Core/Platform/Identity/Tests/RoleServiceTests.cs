using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Tests;

public sealed class RoleServiceTests : IAsyncLifetime
{
    private const string DbName = "CpeIdentityRoleTests";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static IdentityDbContext Context() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>().UseSqlServer(ConnectionString).Options);

    private static RoleService Service(IdentityDbContext context) => new(new RoleRepository(context), TimeProvider.System);

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
    public async Task Creating_a_role_persists_its_permission_codes()
    {
        await using var context = Context();
        var created = await Service(context).CreateAsync(
            "Finance", "Approves payouts", ["ops.withdrawals.view", "ops.withdrawals.approve"], Ct);

        created.IsSuccess.ShouldBeTrue();
        created.Value.PermissionCodes.ShouldBe(["ops.withdrawals.view", "ops.withdrawals.approve"], ignoreOrder: true);

        await using var verify = Context();
        var fetched = await Service(verify).GetAsync(created.Value.RoleId, Ct);
        fetched.IsSuccess.ShouldBeTrue();
        fetched.Value.Name.ShouldBe("Finance");
    }

    [Fact]
    public async Task Creating_a_role_with_a_taken_name_fails()
    {
        await using var first = Context();
        await Service(first).CreateAsync("Support", null, [], Ct);

        await using var second = Context();
        var duplicate = await Service(second).CreateAsync("Support", null, [], Ct);

        duplicate.IsFailure.ShouldBeTrue();
        duplicate.Error!.Code.ShouldBe(RoleErrors.NameAlreadyExists.Code);
    }

    [Fact]
    public async Task Setting_permissions_replaces_the_full_set()
    {
        await using var create = Context();
        var role = (await Service(create).CreateAsync("Ops", null, ["ops.merchants.view"], Ct)).Value;

        await using var update = Context();
        var updated = await Service(update).SetPermissionsAsync(role.RoleId, ["ops.merchants.manage"], Ct);

        updated.IsSuccess.ShouldBeTrue();
        updated.Value.PermissionCodes.ShouldBe(["ops.merchants.manage"]);
    }

    [Fact]
    public async Task Deleting_a_role_still_assigned_to_a_staff_user_is_refused()
    {
        await using var context = Context();
        var role = Role.Create("Auditor", null, ["ops.transactions.view"], DateTimeOffset.UtcNow).Value;
        context.Roles.Add(role);
        var user = StaffUser.Create("auditor1", new StaffPasswordHasher().Hash("pw"), role.Id, DateTimeOffset.UtcNow).Value;
        context.StaffUsers.Add(user);
        await context.SaveChangesAsync(Ct);

        await using var deleteContext = Context();
        var result = await Service(deleteContext).DeleteAsync(role.Id, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(RoleErrors.InUse.Code);
    }

    [Fact]
    public async Task Deleting_an_unreferenced_role_succeeds()
    {
        await using var create = Context();
        var role = (await Service(create).CreateAsync("Temp", null, [], Ct)).Value;

        await using var delete = Context();
        (await Service(delete).DeleteAsync(role.RoleId, Ct)).IsSuccess.ShouldBeTrue();

        await using var verify = Context();
        (await Service(verify).GetAsync(role.RoleId, Ct)).IsFailure.ShouldBeTrue();
    }
}
