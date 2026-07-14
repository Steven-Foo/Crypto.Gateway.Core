using CryptoPaymentEngine.Gateway.Core.Blockchain.Domain;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Tests;

public sealed class AssetPersistenceTests : IAsyncLifetime
{
    private const string DbName = "CpeBlockchainAssetTests";

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static BlockchainDbContext NewContext() =>
        new(new DbContextOptionsBuilder<BlockchainDbContext>()
            .UseSqlServer(ConnectionString)
            .UseBigIntegerMoney()
            .Options);

    public async ValueTask InitializeAsync()
    {
        await using var context = NewContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await using var context = NewContext();
        await context.Database.EnsureDeletedAsync();
    }

    private static Asset NewAsset(Chain chain, string symbol, string? contract, int decimals) =>
        Asset.Create(chain, symbol, contract, decimals).Value;

    [Fact]
    public async Task Native_coin_and_tokens_coexist_on_the_same_chain()
    {
        await using var context = NewContext();
        context.Assets.Add(NewAsset(Chain.Tron, "TRX", null, 6));
        context.Assets.Add(NewAsset(Chain.Tron, "USDT", "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t", 6));
        context.Assets.Add(NewAsset(Chain.Ethereum, "ETH", null, 18));

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await context.Assets.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(3);
    }

    /// <summary>
    /// Regression guard: EF's default unique index on a nullable column adds
    /// "WHERE [ContractAddress] IS NOT NULL", which would let two native TRX rows through.
    /// AssetConfiguration strips that filter with HasFilter(null).
    /// </summary>
    [Fact]
    public async Task Duplicate_native_coin_is_rejected_even_though_contract_address_is_null()
    {
        await using (var context = NewContext())
        {
            context.Assets.Add(NewAsset(Chain.Tron, "TRX", null, 6));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = NewContext())
        {
            context.Assets.Add(NewAsset(Chain.Tron, "TRX", null, 6));
            await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Duplicate_token_contract_on_same_chain_is_rejected()
    {
        const string contract = "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t";

        await using (var context = NewContext())
        {
            context.Assets.Add(NewAsset(Chain.Tron, "USDT", contract, 6));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = NewContext())
        {
            context.Assets.Add(NewAsset(Chain.Tron, "USDT", contract, 6));
            await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Same_symbol_on_different_chains_is_allowed()
    {
        await using var context = NewContext();
        context.Assets.Add(NewAsset(Chain.Tron, "USDT", "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t", 6));
        context.Assets.Add(NewAsset(Chain.Ethereum, "USDT", "0xdAC17F958D2ee523a2206206994597C13D831ec7", 6));

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await context.Assets.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    [Fact]
    public async Task Chain_and_status_persist_as_readable_strings()
    {
        await using (var context = NewContext())
        {
            context.Assets.Add(NewAsset(Chain.Solana, "SOL", null, 9));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = NewContext())
        {
            var stored = await context.Database
                .SqlQueryRaw<string>("SELECT Chain AS Value FROM blockchain.Asset")
                .SingleAsync(TestContext.Current.CancellationToken);

            stored.ShouldBe("Solana");
        }
    }

    [Fact]
    public void Asset_creation_rejects_invalid_decimals()
    {
        Asset.Create(Chain.Tron, "BAD", null, 39).IsFailure.ShouldBeTrue();
        Asset.Create(Chain.Tron, "BAD", null, -1).IsFailure.ShouldBeTrue();
        Asset.Create(Chain.Tron, "  ", null, 6).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Native_asset_is_identified_by_absent_contract_address()
    {
        NewAsset(Chain.Tron, "TRX", null, 6).IsNative.ShouldBeTrue();
        NewAsset(Chain.Tron, "USDT", "TR7NHq", 6).IsNative.ShouldBeFalse();
    }
}
