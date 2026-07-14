using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Handlers;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Events;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Persistence;
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
using DepositEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain.Deposit;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Tests;

/// <summary>
/// Proves the durable relay: a <c>DepositConfirmed</c> sitting in Deposit's outbox is dispatched to the
/// in-process event bus and credits the merchant in the Ledger — no manual deserialization, exactly what
/// the host's background <see cref="OutboxDispatcher{TContext}"/> does. Also proves at-least-once safety:
/// a re-dispatch does not double-credit.
/// </summary>
public sealed class OutboxDispatcherTests : IAsyncLifetime
{
    private const string DbName = "CpeOutboxDispatcherTests";
    private static readonly Guid MerchantId = Guid.CreateVersion7();
    private static readonly Guid AssetId = Guid.CreateVersion7();
    private static readonly BigInteger Amount = BigInteger.Parse("1000000");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private ServiceProvider _provider = null!;

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDistributedLockFactory, NoOpLock>();

        services.AddDbContext<DepositDbContext>(o => o.UseSqlServer(ConnectionString).UseBigIntegerMoney());
        services.AddDbContext<LedgerDbContext>(o => o.UseSqlServer(ConnectionString).UseBigIntegerMoney());

        services.AddScoped<ILedgerAccountStore, LedgerAccountStore>();
        services.AddScoped<ILedgerPostingStore, LedgerPostingStore>();
        services.AddScoped<ILedgerPoster, LedgerPoster>();
        services.AddScoped<IIntegrationEventHandler<DepositConfirmed>, DepositConfirmedHandler>();
        services.AddScoped<IEventBus, InProcessEventBus>();

        _provider = services.BuildServiceProvider();

        await using var scope = _provider.CreateAsyncScope();
        var deposit = scope.ServiceProvider.GetRequiredService<DepositDbContext>();
        await deposit.Database.EnsureDeletedAsync(Ct);
        await deposit.Database.EnsureCreatedAsync(Ct);
        var ledger = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        await ledger.Database.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await using (var scope = _provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<DepositDbContext>().Database.EnsureDeletedAsync(Ct);
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task Dispatching_the_outbox_credits_the_merchant_and_marks_the_message_processed()
    {
        await SeedConfirmedDepositAsync();

        var dispatcher = NewDispatcher();
        await dispatcher.DispatchPendingAsync(Ct);

        (await MerchantBalanceAsync()).ShouldBe(Amount);

        await using var scope = _provider.CreateAsyncScope();
        var outbox = await scope.ServiceProvider.GetRequiredService<DepositDbContext>().OutboxMessages.SingleAsync(Ct);
        outbox.ProcessedOnUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_second_dispatch_does_not_double_credit()
    {
        await SeedConfirmedDepositAsync();
        var dispatcher = NewDispatcher();

        await dispatcher.DispatchPendingAsync(Ct);
        await dispatcher.DispatchPendingAsync(Ct); // message already processed → nothing to do

        (await MerchantBalanceAsync()).ShouldBe(Amount); // credited exactly once
    }

    private OutboxDispatcher<DepositDbContext> NewDispatcher() =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(),
            new NoOpLock(),
            TimeProvider.System,
            NullLogger<OutboxDispatcher<DepositDbContext>>.Instance);

    private async Task SeedConfirmedDepositAsync()
    {
        var policy = new DepositPolicy(CreditStrategy.Confirmations, 1, BigInteger.Zero);
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<DepositDbContext>();

        var deposit = DepositEntity.Record(
            Chain.Tron, "TAddr", Guid.CreateVersion7(), MerchantId, AssetId, Amount, "0xtx", 0, 100, "h100", policy, DateTimeOffset.UtcNow).Value;
        deposit.RegisterConfirmations(1, isFinalized: false, policy, DateTimeOffset.UtcNow); // → Confirmed, raises DepositConfirmed

        context.Deposits.Add(deposit);
        await context.SaveChangesAsync(Ct); // interceptor writes the DepositConfirmed outbox row
    }

    private async Task<BigInteger> MerchantBalanceAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        return await ledger.AccountBalances
            .Join(ledger.Accounts, b => b.Id, a => a.Id, (b, a) => new { b, a })
            .Where(x => x.a.AccountType == AccountType.MerchantLiability && x.a.OwnerId == MerchantId && x.a.AssetId == AssetId)
            .Select(x => x.b.Balance)
            .SingleAsync(Ct);
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
