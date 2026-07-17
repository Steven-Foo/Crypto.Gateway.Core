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
        // Prime the cursor past cold-start first (see the dedicated cold-start test below) so this test is
        // purely about ongoing detection: a transfer arriving after the scanner is already caught up.
        chain.AddBlock(Chain.Tron, 99, "h99");
        await using (var ctx = Context())
            await Detection(ctx, chain, WalletsWithWatchedAddress(), Policy).ScanOnceAsync(Chain.Tron, Ct);

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
    public async Task Cold_start_seeds_the_cursor_to_the_current_tip_instead_of_crawling_from_genesis()
    {
        // Simulate a chain far into its history (a real chain's tip is never near block 1) with a transfer
        // already sitting at the tip before the scanner ever ran for this chain.
        var chain = new InMemoryChainSource();
        chain.AddBlock(Chain.Tron, 500_000, "h500000", Transfer(OneUsdt, 500_000, "h500000"));

        (await Detection(Context(), chain, WalletsWithWatchedAddress(), Policy).ScanOnceAsync(Chain.Tron, Ct)).ShouldBe(0);

        await using var verify = Context();
        (await verify.Deposits.CountAsync(Ct)).ShouldBe(0); // not retroactively found — watching starts from here forward
        (await verify.ScanCursors.SingleAsync(Ct)).LastScannedBlock.ShouldBe(500_000); // jumps straight to tip, no genesis crawl
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
        chain.AddBlock(Chain.Tron, 99, "h99"); // prime past cold start (see the dedicated cold-start test)
        await using (var ctx = Context())
            await Detection(ctx, chain, WalletsWithWatchedAddress(), Policy).ScanOnceAsync(Chain.Tron, Ct);

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
        chain.AddBlock(Chain.Tron, 99, "h99"); // prime past cold start (see the dedicated cold-start test)
        await using (var ctx = Context())
            await Detection(ctx, chain, WalletsWithWatchedAddress(), Policy).ScanOnceAsync(Chain.Tron, Ct);

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
    public async Task A_deposit_whose_block_is_irreversible_is_retired_from_the_tracker()
    {
        // The tracker re-reads one block per tracked deposit per pass. A deposit on a solidified/finalized
        // block can never reorg, so leaving it tracked would burn an RPC per pass forever and grow without
        // bound as deposits accumulate — eventually exhausting the node's rate limit.
        var chain = new InMemoryChainSource();
        chain.AddBlock(Chain.Tron, 99, "h99");
        await using (var ctx = Context())
            await Detection(ctx, chain, WalletsWithWatchedAddress(), Policy).ScanOnceAsync(Chain.Tron, Ct);

        chain.AddBlock(Chain.Tron, 100, "h100", Transfer(OneUsdt, 100, "h100"));
        await using (var ctx = Context())
            await Detection(ctx, chain, WalletsWithWatchedAddress(), Policy).ScanOnceAsync(Chain.Tron, Ct);

        chain.AddBlock(Chain.Tron, 101, "h101");
        chain.AddBlock(Chain.Tron, 102, "h102");

        // Confirms (3 deep) but block 100 is not yet solidified → still watched for reorgs.
        await using (var ctx = Context())
            await Confirmation(ctx, chain, Policy).TrackOnceAsync(Chain.Tron, Ct);

        await using (var ctx = Context())
        {
            var stillTracked = await new DepositRepository(ctx).GetTrackableAsync(Chain.Tron, Ct);
            stillTracked.ShouldHaveSingleItem().IsFinalized.ShouldBeFalse();
        }

        // The chain now solidifies block 100 — it is irreversible from here.
        chain.SetFinalizedHeight(Chain.Tron, 100);

        await using (var ctx = Context())
            await Confirmation(ctx, chain, Policy).TrackOnceAsync(Chain.Tron, Ct);

        await using (var verify = Context())
        {
            var deposit = await verify.Deposits.SingleAsync(Ct);
            deposit.Status.ShouldBe(DepositStatus.Confirmed); // money state untouched — it stays credited
            deposit.IsFinalized.ShouldBeTrue();

            // The point of the fix: the tracker's working set is now empty, so subsequent passes cost no RPC.
            (await new DepositRepository(verify).GetTrackableAsync(Chain.Tron, Ct)).ShouldBeEmpty();

            // And still exactly one credit — settling must not re-raise anything.
            (await verify.OutboxMessages.CountAsync(Ct)).ShouldBe(1);
        }
    }

    [Fact]
    public async Task A_reorg_orphans_a_confirmed_deposit_and_writes_a_DepositOrphaned_outbox_message()
    {
        var chain = new InMemoryChainSource();
        chain.AddBlock(Chain.Tron, 99, "h99"); // prime past cold start (see the dedicated cold-start test)
        await using (var ctx = Context())
            await Detection(ctx, chain, WalletsWithWatchedAddress(), Policy).ScanOnceAsync(Chain.Tron, Ct);

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
