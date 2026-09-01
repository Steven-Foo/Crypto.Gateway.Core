using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Infrastructure.Persistence;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;
using WalletEntity = CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain.Wallet;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Tests;

/// <summary>
/// The Ops wallet search, and specifically its <c>WalletType</c> narrowing (REQ-13). Without it the admin
/// screen could only narrow the page it had already loaded, so "show me the withdrawal hot pool" silently
/// meant "show me whichever hot-pool wallets happen to be on page 1" — the filter has to run in SQL over the
/// whole set, and the total count has to reflect it.
/// </summary>
public sealed class WalletAdminSearchTests : IAsyncLifetime
{
    private const string DbName = "CpeWalletAdminSearchTests";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private WalletDbContext _context = null!;
    private WalletRepository _repository = null!;

    public async ValueTask InitializeAsync()
    {
        _context = new WalletDbContext(
            new DbContextOptionsBuilder<WalletDbContext>().UseSqlServer(ConnectionString).Options);
        await _context.Database.EnsureDeletedAsync(Ct);
        await _context.Database.EnsureCreatedAsync(Ct);
        _repository = new WalletRepository(_context);
    }

    public async ValueTask DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync(Ct);
        await _context.DisposeAsync();
    }

    private static WalletAdminFilter Unfiltered => new(null, null, null, null);


    private async Task SeedAsync()
    {
        var merchant = Guid.CreateVersion7();

        _context.Wallets.AddRange(
            WalletEntity.CreateDeposit(Guid.CreateVersion7(), Chain.Tron, "TDeposit1", merchant).Value,
            WalletEntity.CreateDeposit(Guid.CreateVersion7(), Chain.Tron, "TDeposit2", merchant).Value,
            WalletEntity.CreatePlatform(Guid.CreateVersion7(), Chain.Tron, "THot1", WalletType.HotWithdrawal).Value,
            WalletEntity.CreatePlatform(Guid.CreateVersion7(), Chain.Tron, "THot2", WalletType.HotWithdrawal).Value,
            WalletEntity.CreatePlatform(Guid.CreateVersion7(), Chain.Tron, "TEnergy1", WalletType.Energy).Value);

        await _context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Filtering_by_wallet_type_returns_only_that_type()
    {
        await SeedAsync();

        var (rows, totalCount) = await _repository.SearchAsync(
            Unfiltered with { WalletType = WalletType.HotWithdrawal }, 1, 50, Ct);

        rows.Select(w => w.Address).OrderBy(a => a).ShouldBe(["THot1", "THot2"]);
        rows.ShouldAllBe(w => w.WalletType == "HotWithdrawal");
        totalCount.ShouldBe(2);
    }

    /// <summary>
    /// The point of pushing the filter into SQL: the count must be the count of MATCHES, not of everything.
    /// A page-local filter would report 5 here and page the operator through empty results.
    /// </summary>
    [Fact]
    public async Task The_total_count_reflects_the_wallet_type_filter_not_the_whole_table()
    {
        await SeedAsync();

        var (_, unfilteredTotal) = await _repository.SearchAsync(Unfiltered, 1, 1, Ct);
        var (rows, filteredTotal) = await _repository.SearchAsync(
            Unfiltered with { WalletType = WalletType.Energy }, 1, 1, Ct);

        unfilteredTotal.ShouldBe(5);
        filteredTotal.ShouldBe(1);
        rows.Single().Address.ShouldBe("TEnergy1");
    }

    [Fact]
    public async Task Omitting_the_wallet_type_returns_every_type()
    {
        await SeedAsync();

        var (rows, totalCount) = await _repository.SearchAsync(Unfiltered, 1, 50, Ct);

        totalCount.ShouldBe(5);
        rows.Select(w => w.WalletType).Distinct().OrderBy(t => t)
            .ShouldBe(["Deposit", "Energy", "HotWithdrawal"]);
    }

    /// <summary>The by-id narrowing that backs the single-record detail endpoint (REQ-6).</summary>
    [Fact]
    public async Task Filtering_by_wallet_id_returns_exactly_that_wallet()
    {
        await SeedAsync();
        var target = await _context.Wallets.SingleAsync(w => w.Address == "TEnergy1", Ct);

        var (rows, totalCount) = await _repository.SearchAsync(
            Unfiltered with { WalletId = target.Id }, 1, 50, Ct);

        totalCount.ShouldBe(1);
        rows.Single().WalletId.ShouldBe(target.Id);
        rows.Single().Address.ShouldBe("TEnergy1");
    }

    /// <summary>An id that does not exist is an empty result, which the endpoint turns into a 404 — never a
    /// silently unfiltered list.</summary>
    [Fact]
    public async Task An_unknown_wallet_id_matches_nothing()
    {
        await SeedAsync();

        var (rows, totalCount) = await _repository.SearchAsync(
            Unfiltered with { WalletId = Guid.CreateVersion7() }, 1, 50, Ct);

        rows.ShouldBeEmpty();
        totalCount.ShouldBe(0);
    }

    /// <summary>Wallet type and status are independent narrowings that AND together — a suspended deposit
    /// address must not surface in a search for suspended hot wallets.</summary>
    [Fact]
    public async Task Wallet_type_and_status_narrow_together()
    {
        await SeedAsync();

        var suspendedDeposit = await _context.Wallets.SingleAsync(w => w.Address == "TDeposit1", Ct);
        suspendedDeposit.Suspend("under investigation", DateTimeOffset.UtcNow);
        await _context.SaveChangesAsync(Ct);

        var (depositRows, _) = await _repository.SearchAsync(
            Unfiltered with { WalletType = WalletType.Deposit, Status = WalletStatus.Suspended }, 1, 50, Ct);
        var (hotRows, _) = await _repository.SearchAsync(
            Unfiltered with { WalletType = WalletType.HotWithdrawal, Status = WalletStatus.Suspended }, 1, 50, Ct);

        depositRows.Single().Address.ShouldBe("TDeposit1");
        hotRows.ShouldBeEmpty();
    }
}
