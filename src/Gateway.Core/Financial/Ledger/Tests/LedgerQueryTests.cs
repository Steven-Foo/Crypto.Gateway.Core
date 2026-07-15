using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Tests;

/// <summary>
/// Proves the public balance read model (<see cref="ILedgerQuery"/>) that a host's <c>/balance</c> endpoint
/// derives from — never a stored column. It must equal credited deposits minus what is reserved/settled,
/// i.e. the merchant's <em>available</em> balance.
/// </summary>
public sealed class LedgerQueryTests : LedgerTestHost
{
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly BigInteger Deposited = BigInteger.Parse("10000000"); // 10 USDT
    private static readonly BigInteger Amount = BigInteger.Parse("3000000");     // 3
    private static readonly BigInteger Fee = BigInteger.Parse("100000");         // 0.1

    [Fact]
    public async Task A_merchant_with_no_ledger_activity_has_a_zero_balance()
    {
        await using var ctx = Context();
        (await new LedgerQuery(ctx).GetMerchantBalanceAsync(Merchant, Asset, Ct)).ShouldBe(BigInteger.Zero);
    }

    [Fact]
    public async Task The_balance_equals_the_credited_deposit()
    {
        await using (var ctx = Context())
            (await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, Deposited), Ct))
                .IsSuccess.ShouldBeTrue();

        await using var verify = Context();
        (await new LedgerQuery(verify).GetMerchantBalanceAsync(Merchant, Asset, Ct)).ShouldBe(Deposited);
    }

    [Fact]
    public async Task A_reserved_withdrawal_is_excluded_so_the_query_returns_the_available_balance()
    {
        await using (var ctx = Context())
            await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, Deposited), Ct);
        await using (var ctx = Context())
            await ((IWithdrawalLedger)Poster(ctx)).ReserveAsync(new ReserveWithdrawalRequest(Guid.CreateVersion7(), Merchant, Asset, Amount, Fee), Ct);

        await using var verify = Context();
        (await new LedgerQuery(verify).GetMerchantBalanceAsync(Merchant, Asset, Ct)).ShouldBe(Deposited - (Amount + Fee));
    }

    [Fact]
    public async Task Balances_are_isolated_per_merchant_and_per_asset()
    {
        await using (var ctx = Context())
            await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, Deposited), Ct);

        await using var verify = Context();
        var query = new LedgerQuery(verify);
        (await query.GetMerchantBalanceAsync(Merchant, Guid.CreateVersion7(), Ct)).ShouldBe(BigInteger.Zero); // other asset
        (await query.GetMerchantBalanceAsync(Guid.CreateVersion7(), Asset, Ct)).ShouldBe(BigInteger.Zero);    // other merchant
    }
}
