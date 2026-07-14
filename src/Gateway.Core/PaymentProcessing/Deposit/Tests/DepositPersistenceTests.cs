using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Persistence;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;
using DepositEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain.Deposit;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Tests;

public sealed class DepositPersistenceTests : DepositTestHost
{
    private const string Address = "TWatchedAddr";
    private static readonly Guid WalletId = Guid.CreateVersion7();
    private static readonly Guid MerchantId = Guid.CreateVersion7();
    private static readonly Guid AssetId = Guid.CreateVersion7();
    private static readonly BigInteger OneUsdt = BigInteger.Parse("1000000");
    private static readonly DepositPolicy Policy = new(CreditStrategy.Confirmations, 3, BigInteger.Parse("1000"));

    private static FakeWalletDirectory WalletsWithWatchedAddress() =>
        new FakeWalletDirectory().Register(
            new WalletOwnership(WalletId, Guid.CreateVersion7(), Chain.Tron, Address, "Deposit", MerchantId, IsActive: true));

    private static DetectedTransfer Transfer(BigInteger amount, long block, string blockHash, int outputIndex = 0, string address = Address) =>
        new(Chain.Tron, address, AssetId, amount, $"0xtx{block}_{outputIndex}", outputIndex, block, blockHash);

    [Fact]
    public async Task Detection_records_a_deposit_for_a_watched_address_and_advances_the_cursor()
    {
        var chain = new InMemoryChainSource();
        chain.AddBlock(Chain.Tron, 100, "h100", Transfer(OneUsdt, 100, "h100"));

        await using (var ctx = Context())
            (await Detection(ctx, chain, WalletsWithWatchedAddress(), Policy).ScanOnceAsync(Chain.Tron, Ct)).ShouldBe(1);

        await using (var verify = Context())
        {
            var deposit = await verify.Deposits.SingleAsync(Ct);
            deposit.Status.ShouldBe(DepositStatus.Detected);
            deposit.MerchantId.ShouldBe(MerchantId);
            deposit.WalletId.ShouldBe(WalletId);
            deposit.Amount.ShouldBe(OneUsdt);
            deposit.BlockHash.ShouldBe("h100");

            (await verify.ScanCursors.SingleAsync(Ct)).LastScannedBlock.ShouldBe(100);
        }
    }

    [Fact]
    public async Task Detection_ignores_transfers_to_unknown_addresses()
    {
        var chain = new InMemoryChainSource();
        chain.AddBlock(Chain.Tron, 100, "h100", Transfer(OneUsdt, 100, "h100", address: "TSomeoneElse"));

        await using (var ctx = Context())
            (await Detection(ctx, chain, WalletsWithWatchedAddress(), Policy).ScanOnceAsync(Chain.Tron, Ct)).ShouldBe(0);

        await using (var verify = Context())
            (await verify.Deposits.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Detection_records_dust_below_the_minimum_as_ignored()
    {
        var chain = new InMemoryChainSource();
        chain.AddBlock(Chain.Tron, 100, "h100", Transfer(BigInteger.Parse("999"), 100, "h100"));

        await using (var ctx = Context())
            await Detection(ctx, chain, WalletsWithWatchedAddress(), Policy).ScanOnceAsync(Chain.Tron, Ct);

        await using (var verify = Context())
            (await verify.Deposits.SingleAsync(Ct)).Status.ShouldBe(DepositStatus.Ignored);
    }

    [Fact]
    public async Task The_same_on_chain_output_is_recorded_only_once()
    {
        var deposit = DepositEntity.Record(
            Chain.Tron, Address, WalletId, MerchantId, AssetId, OneUsdt, "0xdup", 0, 100, "h100", Policy, DateTimeOffset.UtcNow).Value;
        var again = DepositEntity.Record(
            Chain.Tron, Address, WalletId, MerchantId, AssetId, OneUsdt, "0xdup", 0, 100, "h100", Policy, DateTimeOffset.UtcNow).Value;

        await using var ctx = Context();
        var repo = new DepositRepository(ctx);

        (await repo.AddIfNewAsync(deposit, Ct)).ShouldBe(DepositRecordOutcome.Recorded);
        (await repo.AddIfNewAsync(again, Ct)).ShouldBe(DepositRecordOutcome.Duplicate);

        await using var verify = Context();
        (await verify.Deposits.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_deposit_confirms_at_the_threshold_and_writes_a_DepositConfirmed_outbox_message()
    {
        var chain = new InMemoryChainSource();
        chain.AddBlock(Chain.Tron, 100, "h100", Transfer(OneUsdt, 100, "h100"));

        await using (var ctx = Context())
            await Detection(ctx, chain, WalletsWithWatchedAddress(), Policy).ScanOnceAsync(Chain.Tron, Ct);

        // Advance the chain to 3 confirmations (102 - 100 + 1), leaving block 100 canonical.
        chain.AddBlock(Chain.Tron, 101, "h101");
        chain.AddBlock(Chain.Tron, 102, "h102");

        await using (var ctx = Context())
            (await Confirmation(ctx, chain, Policy).TrackOnceAsync(Chain.Tron, Ct)).ShouldBe(1);

        await using (var verify = Context())
        {
            var deposit = await verify.Deposits.SingleAsync(Ct);
            deposit.Status.ShouldBe(DepositStatus.Confirmed);
            deposit.Confirmations.ShouldBe(3);

            var outbox = await verify.OutboxMessages.SingleAsync(Ct);
            outbox.Type.ShouldContain("DepositConfirmed");
            outbox.Content.ShouldContain(OneUsdt.ToString()); // exact base-unit amount on the wire
        }
    }

    [Fact]
    public async Task A_reorg_orphans_a_confirmed_deposit_and_writes_a_DepositOrphaned_outbox_message()
    {
        var chain = new InMemoryChainSource();
        chain.AddBlock(Chain.Tron, 100, "h100", Transfer(OneUsdt, 100, "h100"));
        await using (var ctx = Context())
            await Detection(ctx, chain, WalletsWithWatchedAddress(), Policy).ScanOnceAsync(Chain.Tron, Ct);

        chain.AddBlock(Chain.Tron, 101, "h101");
        chain.AddBlock(Chain.Tron, 102, "h102");
        await using (var ctx = Context())
            await Confirmation(ctx, chain, Policy).TrackOnceAsync(Chain.Tron, Ct); // now Confirmed

        // Reorg: block 100 comes back with a different hash.
        chain.ReplaceBlock(Chain.Tron, 100, "h100_reorged", Transfer(OneUsdt, 100, "h100_reorged"));

        await using (var ctx = Context())
            (await Confirmation(ctx, chain, Policy).TrackOnceAsync(Chain.Tron, Ct)).ShouldBe(1);

        await using (var verify = Context())
        {
            (await verify.Deposits.SingleAsync(Ct)).Status.ShouldBe(DepositStatus.Orphaned);

            var messages = await verify.OutboxMessages.OrderBy(m => EF.Property<long>(m, "Seq")).ToListAsync(Ct);
            messages.Select(m => m.Type).ShouldContain(t => t.Contains("DepositConfirmed"));
            messages.Select(m => m.Type).ShouldContain(t => t.Contains("DepositOrphaned"));
        }
    }
}
