using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Tests;

public sealed class StaffAccountServiceTests : IAsyncLifetime
{
    private const string DbName = "CpeIdentityAccountTests";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static IdentityDbContext Context() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>().UseSqlServer(ConnectionString).Options);

    private static StaffAccountService Service(IdentityDbContext context) =>
        new(new StaffUserRepository(context), new RoleRepository(context), new StaffPasswordHasher(),
            new StaffPasswordGenerator(), TimeProvider.System);

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

    private static async Task<Guid> SeedRoleAsync(string name = "Viewer")
    {
        await using var context = Context();
        var role = Role.Create(name, null, ["ops.merchants.view"], DateTimeOffset.UtcNow).Value;
        context.Roles.Add(role);
        await context.SaveChangesAsync(Ct);
        return role.Id;
    }

    [Fact]
    public async Task Creating_an_account_returns_a_one_time_password_that_actually_logs_in()
    {
        var roleId = await SeedRoleAsync();

        await using var create = Context();
        var created = await Service(create).CreateAsync("ops.person1", roleId, Ct);

        created.IsSuccess.ShouldBeTrue();
        created.Value.Password.ShouldNotBeNullOrWhiteSpace();

        await using var verify = Context();
        var hasher = new StaffPasswordHasher();
        var user = await verify.StaffUsers.SingleAsync(u => u.Username == "ops.person1", Ct);
        hasher.Verify(created.Value.Password, user.PasswordHash).ShouldBeTrue();
        user.Status.ShouldBe(StaffUserStatus.Active);
    }

    [Fact]
    public async Task Creating_an_account_with_a_taken_username_fails()
    {
        var roleId = await SeedRoleAsync();
        await using var first = Context();
        await Service(first).CreateAsync("dupe", roleId, Ct);

        await using var second = Context();
        var duplicate = await Service(second).CreateAsync("dupe", roleId, Ct);

        duplicate.IsFailure.ShouldBeTrue();
        duplicate.Error!.Code.ShouldBe(StaffUserErrors.UsernameAlreadyExists.Code);
    }

    [Fact]
    public async Task An_account_cannot_disable_itself()
    {
        var roleId = await SeedRoleAsync();
        await using var create = Context();
        var account = (await Service(create).CreateAsync("self", roleId, Ct)).Value;

        await using var disable = Context();
        var result = await Service(disable).SetStatusAsync(account.StaffUserId, active: false, account.StaffUserId, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(StaffUserErrors.CannotDisableSelf.Code);
    }

    [Fact]
    public async Task The_last_active_account_cannot_be_disabled()
    {
        var roleId = await SeedRoleAsync();
        await using var create = Context();
        var only = (await Service(create).CreateAsync("only-active", roleId, Ct)).Value;

        // A different caller (not "only") tries to disable the last active account.
        await using var disable = Context();
        var result = await Service(disable).SetStatusAsync(only.StaffUserId, active: false, Guid.CreateVersion7(), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(StaffUserErrors.CannotDisableLastActiveAccount.Code);
    }

    [Fact]
    public async Task Disabling_one_of_two_active_accounts_succeeds()
    {
        var roleId = await SeedRoleAsync();
        await using var create = Context();
        var first = (await Service(create).CreateAsync("first", roleId, Ct)).Value;
        var second = (await Service(create).CreateAsync("second", roleId, Ct)).Value;

        await using var disable = Context();
        var result = await Service(disable).SetStatusAsync(second.StaffUserId, active: false, first.StaffUserId, Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe("Disabled");
    }

    [Fact]
    public async Task Resetting_a_password_invalidates_the_old_one()
    {
        var roleId = await SeedRoleAsync();
        await using var create = Context();
        var created = await Service(create).CreateAsync("reset-me", roleId, Ct);

        await using var reset = Context();
        var newCredential = await Service(reset).ResetPasswordAsync(created.Value.StaffUserId, Ct);

        newCredential.IsSuccess.ShouldBeTrue();
        newCredential.Value.Password.ShouldNotBe(created.Value.Password);

        await using var verify = Context();
        var hasher = new StaffPasswordHasher();
        var user = await verify.StaffUsers.SingleAsync(u => u.Id == created.Value.StaffUserId, Ct);
        hasher.Verify(created.Value.Password, user.PasswordHash).ShouldBeFalse();
        hasher.Verify(newCredential.Value.Password, user.PasswordHash).ShouldBeTrue();
    }

    [Fact]
    public async Task Changing_role_to_an_unknown_role_fails()
    {
        var roleId = await SeedRoleAsync();
        await using var create = Context();
        var account = (await Service(create).CreateAsync("reassign-me", roleId, Ct)).Value;

        await using var change = Context();
        var result = await Service(change).ChangeRoleAsync(account.StaffUserId, Guid.CreateVersion7(), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(RoleErrors.NotFound.Code);
    }
}
