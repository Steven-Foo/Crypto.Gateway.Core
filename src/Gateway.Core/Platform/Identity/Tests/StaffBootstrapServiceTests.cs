using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Tests;

public sealed class StaffBootstrapServiceTests : IAsyncLifetime
{
    private const string DbName = "CpeIdentityBootstrapTests";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static IdentityDbContext Context() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>().UseSqlServer(ConnectionString).Options);

    private static StaffBootstrapService Service(IdentityDbContext context) =>
        new(new StaffUserRepository(context), new RoleRepository(context),
            new StaffPasswordHasher(), new StaffPasswordGenerator(), TimeProvider.System);

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
    public async Task Creates_a_wildcard_admin_that_must_enrol_2fa_and_whose_password_verifies()
    {
        await using var context = Context();
        var result = await Service(context).BootstrapFirstAdminAsync("  owner  ", Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Username.ShouldBe("owner");
        result.Value.Password.ShouldNotBeNullOrWhiteSpace();

        await using var check = Context();
        var user = await new StaffUserRepository(check).FindByUsernameAsync("owner", Ct);
        user.ShouldNotBeNull();
        user.RequireTwoFactor.ShouldBeTrue();
        new StaffPasswordHasher().Verify(result.Value.Password, user.PasswordHash).ShouldBeTrue();

        var role = await new RoleRepository(check).GetByIdAsync(user.RoleId, Ct);
        role!.Name.ShouldBe(StaffBootstrapService.AdminRoleName);
        role.PermissionCodesCsv!.ShouldContain(Role.WildcardPermission);
    }

    [Fact]
    public async Task Refuses_a_second_run_so_it_can_never_be_a_back_door()
    {
        await using (var context = Context())
            (await Service(context).BootstrapFirstAdminAsync("owner", Ct)).IsSuccess.ShouldBeTrue();

        await using var again = Context();
        var second = await Service(again).BootstrapFirstAdminAsync("intruder", Ct);

        second.IsFailure.ShouldBeTrue();
        second.Error!.Code.ShouldBe(StaffUserErrors.BootstrapRefused.Code);
        (await new StaffUserRepository(again).UsernameExistsAsync("intruder", Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Reuses_an_existing_admin_role_rather_than_creating_a_duplicate()
    {
        await using (var seed = Context())
        {
            var repo = new RoleRepository(seed);
            repo.Add(Role.Create(StaffBootstrapService.AdminRoleName, "pre-existing", [Role.WildcardPermission], DateTimeOffset.UtcNow).Value);
            await repo.SaveChangesAsync(Ct);
        }

        await using var context = Context();
        (await Service(context).BootstrapFirstAdminAsync("owner", Ct)).IsSuccess.ShouldBeTrue();
        (await context.Set<Role>().CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Blank_username_is_refused_and_creates_nothing()
    {
        await using var context = Context();
        var result = await Service(context).BootstrapFirstAdminAsync("  ", Ct);

        result.Error!.Code.ShouldBe(StaffUserErrors.UsernameRequired.Code);
        (await context.Set<StaffUser>().CountAsync(Ct)).ShouldBe(0);
    }
}
