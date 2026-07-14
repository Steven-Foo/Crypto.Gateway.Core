using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Locking;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Tests;

/// <summary>
/// Shared real-SQL-Server fixture for the Ledger posting tests. One physical database, the single
/// <c>ledger</c> schema. A <b>no-op lock</b> is used deliberately: it removes the Redis single-flight so
/// the database's rowversion + retry is the only thing preventing a lost update — exactly what the
/// concurrency test must prove.
/// </summary>
public abstract class LedgerTestHost : IAsyncLifetime
{
    // One physical database per test class, so classes run in parallel without stomping each other.
    private string DbName => $"CpeLedgerTests_{GetType().Name}";

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    protected LedgerDbContext Context() =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseSqlServer(ConnectionString).UseBigIntegerMoney().Options);

    /// <summary>A fully-wired poster over a fresh context, with the lock disabled so rowversion is the guard.</summary>
    protected static ILedgerPoster Poster(LedgerDbContext context)
    {
        var postingStore = new LedgerPostingStore(
            context, new NoOpDistributedLockFactory(), TimeProvider.System, NullLogger<LedgerPostingStore>.Instance);
        var accountStore = new LedgerAccountStore(context, TimeProvider.System);
        return new LedgerPoster(accountStore, postingStore, TimeProvider.System);
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

    private sealed class NoOpDistributedLockFactory : IDistributedLockFactory
    {
        public Task<IAsyncDisposable> AcquireAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable>(NoOpHandle.Instance);

        private sealed class NoOpHandle : IAsyncDisposable
        {
            public static readonly NoOpHandle Instance = new();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
