using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Events;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application.Handlers;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Tests;

/// <summary>
/// The invoice flow against real SQL Server: address reservation and reuse (the concentration strategy),
/// idempotency, matching a confirmed deposit, and the redelivery guard that stops an old deposit from
/// hijacking a newer invoice on a reused address. Wallet's provisioning Contract is substituted (each call
/// mints a distinct address), so this stays focused on PaymentIntent + its DB indexes.
/// </summary>
public sealed class PaymentIntentFlowTests : IAsyncLifetime
{
    private const string DbName = "CpePaymentIntentTests";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly BigInteger OneUsdt = BigInteger.Parse("1000000");

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static PaymentIntentDbContext Context() =>
        new(new DbContextOptionsBuilder<PaymentIntentDbContext>().UseSqlServer(ConnectionString).UseBigIntegerMoney().Options);

    private sealed class MintCounter { public int Value; }

    private static IDepositAddressProvisioner Provisioner(MintCounter counter)
    {
        var provisioner = Substitute.For<IDepositAddressProvisioner>();
        provisioner.ProvisionDepositAddressAsync(Arg.Any<Guid>(), Arg.Any<Chain>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                counter.Value++;
                return Result.Success(new ProvisionedDepositAddress(Guid.CreateVersion7(), Chain.Tron, "T" + Guid.NewGuid().ToString("N")[..19]));
            });
        return provisioner;
    }

    private static PaymentIntentService Service(PaymentIntentDbContext context, IDepositAddressProvisioner provisioner) =>
        new(new PaymentIntentRepository(context), provisioner,
            Options.Create(new PaymentIntentOptions { ExpiryMinutes = 30 }),
            TimeProvider.System, NullLogger<PaymentIntentService>.Instance);

    private static PaymentIntentMatchHandler Handler(PaymentIntentDbContext context) =>
        new(new PaymentIntentRepository(context), TimeProvider.System, NullLogger<PaymentIntentMatchHandler>.Instance);

    private static async Task<Guid> WalletOfAsync(Guid reference)
    {
        await using var context = Context();
        return (await context.PaymentIntents.AsNoTracking().SingleAsync(i => i.PublicReference == reference, Ct)).WalletId;
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

    [Fact]
    public async Task Creating_an_intent_reserves_an_address_and_persists_it_waiting()
    {
        var counter = new MintCounter();
        await using var context = Context();

        var result = await Service(context, Provisioner(counter)).CreateAsync(
            new CreatePaymentIntentCommand(Merchant, "tx-1", Chain.Tron, Asset, OneUsdt, "https://m.test/cb"), Ct);

        result.IsSuccess.ShouldBeTrue();
        counter.Value.ShouldBe(1);

        await using var verify = Context();
        var intent = await verify.PaymentIntents.SingleAsync(Ct);
        intent.Status.ShouldBe(PaymentIntentStatus.Waiting);
        intent.PublicReference.ShouldBe(result.Value.Reference);
        intent.Address.ShouldBe(result.Value.Address);
    }

    [Fact]
    public async Task The_same_merchant_reference_is_idempotent()
    {
        var counter = new MintCounter();
        await using var context = Context();
        var service = Service(context, Provisioner(counter));

        var first = await service.CreateAsync(new CreatePaymentIntentCommand(Merchant, "tx-dup", Chain.Tron, Asset, OneUsdt, null), Ct);
        var second = await service.CreateAsync(new CreatePaymentIntentCommand(Merchant, "tx-dup", Chain.Tron, Asset, OneUsdt, null), Ct);

        second.Value.Reference.ShouldBe(first.Value.Reference);
        counter.Value.ShouldBe(1); // the replay minted nothing

        await using var verify = Context();
        (await verify.PaymentIntents.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_freed_address_is_reused_by_the_next_invoice()
    {
        var counter = new MintCounter();
        var provisioner = Provisioner(counter);

        var a = await CreateAsync(provisioner, "tx-a");
        var walletA = await WalletOfAsync(a);

        // Match A → its address is free again.
        await using (var context = Context())
        {
            var intent = await new PaymentIntentRepository(context).FindWaitingByWalletAsync(walletA, Ct);
            intent!.MatchTo(Guid.CreateVersion7(), "0xtx", OneUsdt, DateTimeOffset.UtcNow);
            await context.SaveChangesAsync(Ct);
        }

        await CreateAsync(provisioner, "tx-b");

        counter.Value.ShouldBe(1); // B reused A's address; nothing new minted
        await using var verify = Context();
        (await verify.PaymentIntents.SingleAsync(i => i.MerchantTransactionId == "tx-b", Ct)).WalletId.ShouldBe(walletA);
    }

    [Fact]
    public async Task A_confirmed_deposit_matches_the_waiting_invoice()
    {
        var counter = new MintCounter();
        var reference = await CreateAsync(Provisioner(counter), "tx-m");
        var walletId = await WalletOfAsync(reference);

        await using (var context = Context())
            await Handler(context).HandleAsync(DepositTo(walletId, "1000000"), Ct);

        await using var verify = Context();
        var intent = await verify.PaymentIntents.SingleAsync(i => i.PublicReference == reference, Ct);
        intent.Status.ShouldBe(PaymentIntentStatus.Matched);
        intent.AmountMatched.ShouldBe(true);
    }

    [Fact]
    public async Task A_redelivered_old_deposit_cannot_hijack_a_newer_invoice_on_the_reused_address()
    {
        var counter = new MintCounter();
        var provisioner = Provisioner(counter);

        var a = await CreateAsync(provisioner, "tx-a2");
        var walletId = await WalletOfAsync(a);
        var deposit = DepositTo(walletId, "1000000");

        await using (var context = Context()) await Handler(context).HandleAsync(deposit, Ct); // matches A, frees address
        await CreateAsync(provisioner, "tx-b2");                                                // B reuses the address
        await using (var context = Context()) await Handler(context).HandleAsync(deposit, Ct); // redeliver the OLD deposit

        counter.Value.ShouldBe(1); // B reused A's address
        await using var verify = Context();
        var b = await verify.PaymentIntents.SingleAsync(i => i.MerchantTransactionId == "tx-b2", Ct);
        b.WalletId.ShouldBe(walletId);
        b.Status.ShouldBe(PaymentIntentStatus.Waiting); // untouched by the redelivered old deposit
    }

    [Fact]
    public async Task Expiry_frees_a_lapsed_invoices_address_for_reuse()
    {
        var counter = new MintCounter();
        var provisioner = Provisioner(counter);

        var reference = await CreateAsync(provisioner, "tx-exp");
        var walletId = await WalletOfAsync(reference);

        await using (var context = Context())
            (await new PaymentIntentRepository(context).ExpireStaleAsync(DateTimeOffset.UtcNow.AddHours(1), 100, Ct)).ShouldBe(1);

        await CreateAsync(provisioner, "tx-after-exp");

        counter.Value.ShouldBe(1); // reused the expired invoice's address
        await using var verify = Context();
        (await verify.PaymentIntents.SingleAsync(i => i.MerchantTransactionId == "tx-after-exp", Ct)).WalletId.ShouldBe(walletId);
    }

    private static async Task<Guid> CreateAsync(IDepositAddressProvisioner provisioner, string reference)
    {
        await using var context = Context();
        var result = await Service(context, provisioner).CreateAsync(
            new CreatePaymentIntentCommand(Merchant, reference, Chain.Tron, Asset, OneUsdt, null), Ct);
        result.IsSuccess.ShouldBeTrue();
        return result.Value.Reference;
    }

    private static DepositConfirmed DepositTo(Guid walletId, string amountBaseUnits) =>
        new(Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.CreateVersion7(), walletId, Merchant, Asset,
            amountBaseUnits, Chain.Tron, "0x" + Guid.NewGuid().ToString("N"), 0, DateTimeOffset.UtcNow);
}
