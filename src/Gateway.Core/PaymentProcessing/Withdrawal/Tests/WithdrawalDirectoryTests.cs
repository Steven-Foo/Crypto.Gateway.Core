using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Tests;

/// <summary>
/// The merchant transaction-query directory lookup, disambiguated by withdrawal kind. A user payout and a
/// merchant cash-out can share one reference (the idempotency key is <c>(merchant, kind, reference)</c>), so
/// <see cref="WithdrawalDirectory.FindByMerchantReferenceAsync"/> must return the record matching the requested
/// kind — never the wrong kind, which would leak one flow's transaction into the other.
/// </summary>
public sealed class WithdrawalDirectoryTests : IAsyncLifetime
{
    private const string DbName = "CpeWithdrawalDirectoryTests";
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private WithdrawalDbContext _context = null!;
    private WithdrawalDirectory _directory = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<WithdrawalDbContext>()
            .UseSqlServer(ConnectionString).UseBigIntegerMoney().Options;
        _context = new WithdrawalDbContext(options);
        await _context.Database.EnsureDeletedAsync(Ct);
        await _context.Database.EnsureCreatedAsync(Ct);
        _directory = new WithdrawalDirectory(_context);
    }

    public async ValueTask DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync(Ct);
        await _context.DisposeAsync();
    }

    private WithdrawalEntity Persisted(string reference, WithdrawalKind kind, string destination, string amount)
    {
        var withdrawal = WithdrawalEntity.Request(
            Merchant, Asset, Chain.Tron, destination, BigInteger.Parse(amount), BigInteger.Parse("10000"),
            reference, callbackUrl: null, DateTimeOffset.UtcNow, kind).Value;
        _context.Withdrawals.Add(withdrawal);
        return withdrawal;
    }

    [Fact]
    public async Task Lookup_returns_the_record_matching_the_requested_kind_when_a_reference_is_shared()
    {
        const string reference = "ORDER-SHARED";
        var payout = Persisted(reference, WithdrawalKind.User, "TUserDest", "1000000");
        var cashOut = Persisted(reference, WithdrawalKind.Merchant, "TMerchantDest", "2000000");
        await _context.SaveChangesAsync(Ct);

        var foundUser = await _directory.FindByMerchantReferenceAsync(Merchant, reference, "User", Ct);
        var foundMerchant = await _directory.FindByMerchantReferenceAsync(Merchant, reference, "Merchant", Ct);

        foundUser.ShouldNotBeNull();
        foundUser!.WithdrawalId.ShouldBe(payout.Id);
        foundUser.DestinationAddress.ShouldBe("TUserDest");

        foundMerchant.ShouldNotBeNull();
        foundMerchant!.WithdrawalId.ShouldBe(cashOut.Id);
        foundMerchant.DestinationAddress.ShouldBe("TMerchantDest");
    }

    [Fact]
    public async Task Default_kind_is_user()
    {
        const string reference = "ORDER-DEFAULT";
        var payout = Persisted(reference, WithdrawalKind.User, "TUserDest", "1000000");
        await _context.SaveChangesAsync(Ct);

        var found = await _directory.FindByMerchantReferenceAsync(Merchant, reference, cancellationToken: Ct);

        found.ShouldNotBeNull();
        found!.WithdrawalId.ShouldBe(payout.Id);
    }

    [Fact]
    public async Task Merchant_kind_does_not_match_a_user_only_reference()
    {
        const string reference = "ORDER-USER-ONLY";
        Persisted(reference, WithdrawalKind.User, "TUserDest", "1000000");
        await _context.SaveChangesAsync(Ct);

        var found = await _directory.FindByMerchantReferenceAsync(Merchant, reference, "Merchant", Ct);

        found.ShouldBeNull();
    }
}
