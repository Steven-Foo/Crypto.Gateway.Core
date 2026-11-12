using System.Numerics;
using System.Text;
using System.Text.Json;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Tests;

/// <summary>
/// The real TRON energy/bandwidth adapters (§8): the resource reader that the sweep gate + monitor consume,
/// and the freeze/delegate builder that acquires and routes energy. Exercised against an
/// <see cref="ITronResourceRpc"/> fake — the live-node round-trip is a staging concern like the other adapters.
/// Money-adjacent: a wrong resource read parks (or wrongly proceeds) a sweep; a wrong build stakes/delegates
/// the wrong amount to the wrong address.
/// </summary>
public sealed class TronResourceAdapterTests
{
    private const string Staker = "TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH";
    private const string Deposit = "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ITronResourceRpc _rpc = Substitute.For<ITronResourceRpc>();

    // ── Resource reader ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Reads_energy_bandwidth_and_trx_balance_into_the_snapshot()
    {
        _rpc.GetAccountResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TronAccountResourceDto
            {
                EnergyLimit = 500_000,
                EnergyUsed = 100_000,
                FreeNetLimit = 600, FreeNetUsed = 200,   // free bandwidth: 400 left
                NetLimit = 1_000, NetUsed = 300,          // staked bandwidth: 700 left
            });
        _rpc.GetAccountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TronAccountDto
            {
                Balance = 12_345_678, // sun
                FrozenV2 =
                [
                    new TronFrozenV2Dto { Type = "ENERGY", Amount = 100_000_000 },
                    new TronFrozenV2Dto { Type = null, Amount = 20_000_000 }, // no type ⇒ BANDWIDTH
                ],
            });

        var snap = await new TronAccountResourceReader(_rpc, TimeProvider.System).GetAsync(Chain.Tron, Staker, Ct);

        snap.EnergyAvailable.ShouldBe(new BigInteger(400_000));          // 500k − 100k
        snap.BandwidthAvailable.ShouldBe(new BigInteger(1_100));          // (600−200) + (1000−300)
        snap.AvailableTrxBalance.ShouldBe(new BigInteger(12_345_678));
        snap.FrozenTrxForEnergy.ShouldBe(new BigInteger(100_000_000));
        snap.FrozenTrxForBandwidth.ShouldBe(new BigInteger(20_000_000));

        // The native API is addressed by the 21-byte 41… form (visible=false).
        await _rpc.Received(1).GetAccountResourceAsync(TronAddress.ToRawHex(Staker), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_brand_new_account_reads_all_zero_never_an_error()
    {
        _rpc.GetAccountResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new TronAccountResourceDto());
        _rpc.GetAccountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new TronAccountDto());

        var snap = await new TronAccountResourceReader(_rpc, TimeProvider.System).GetAsync(Chain.Tron, Deposit, Ct);

        snap.EnergyAvailable.ShouldBe(BigInteger.Zero);
        snap.BandwidthAvailable.ShouldBe(BigInteger.Zero);
        snap.AvailableTrxBalance.ShouldBe(BigInteger.Zero);
    }

    [Fact]
    public async Task A_non_Tron_chain_is_rejected_by_the_reader()
    {
        await Should.ThrowAsync<ArgumentException>(async () =>
            await new TronAccountResourceReader(_rpc, TimeProvider.System).GetAsync(Chain.Ethereum, Staker, Ct));
    }

    // ── Freeze / delegate builder ────────────────────────────────────────────────

    private TronResourceOperationBuilder Builder =>
        new(_rpc, NullLogger<TronResourceOperationBuilder>.Instance);

    [Fact]
    public async Task Builds_a_freeze_for_energy_with_the_owner_and_amount_in_sun()
    {
        _rpc.FreezeBalanceV2Async(Arg.Any<FreezeBalanceV2Request>(), Arg.Any<CancellationToken>())
            .Returns(TxJson("{\"txID\":\"aa\",\"raw_data_hex\":\"00\"}"));

        var unsigned = await Builder.BuildStakeForEnergyAsync(
            new StakeForEnergyRequest(Chain.Tron, Staker, new BigInteger(100_000_000)), Ct);

        Encoding.UTF8.GetString(unsigned.Payload).ShouldContain("\"txID\":\"aa\""); // the unsigned tx flows to the signer

        await _rpc.Received(1).FreezeBalanceV2Async(
            Arg.Is<FreezeBalanceV2Request>(r =>
                r.OwnerAddress == TronAddress.ToRawHex(Staker) && r.FrozenBalance == 100_000_000 && r.Resource == "ENERGY"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Builds_a_delegate_of_energy_from_owner_to_receiver()
    {
        _rpc.DelegateResourceAsync(Arg.Any<DelegateResourceRequest>(), Arg.Any<CancellationToken>())
            .Returns(TxJson("{\"txID\":\"bb\",\"raw_data_hex\":\"01\"}"));

        await Builder.BuildDelegateEnergyAsync(
            new DelegateEnergyRequest(Chain.Tron, Staker, Deposit, new BigInteger(20_000_000)), Ct);

        await _rpc.Received(1).DelegateResourceAsync(
            Arg.Is<DelegateResourceRequest>(r =>
                r.OwnerAddress == TronAddress.ToRawHex(Staker) &&
                r.ReceiverAddress == TronAddress.ToRawHex(Deposit) &&
                r.Balance == 20_000_000 && r.Resource == "ENERGY"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_node_rejection_error_object_throws_rather_than_returning_a_bogus_tx()
    {
        // "cannot delegate" hex-ascii-encoded, the way TRON returns errors.
        var errorHex = Convert.ToHexString("cannot delegate"u8).ToLowerInvariant();
        _rpc.DelegateResourceAsync(Arg.Any<DelegateResourceRequest>(), Arg.Any<CancellationToken>())
            .Returns(TxJson($"{{\"Error\":\"{errorHex}\"}}"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await Builder.BuildDelegateEnergyAsync(
            new DelegateEnergyRequest(Chain.Tron, Staker, Deposit, new BigInteger(20_000_000)), Ct));
        ex.Message.ShouldContain("cannot delegate");
    }

    [Fact]
    public async Task A_non_positive_stake_amount_is_rejected()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await Builder.BuildStakeForEnergyAsync(
            new StakeForEnergyRequest(Chain.Tron, Staker, BigInteger.Zero), Ct));
    }

    // ── Native-TRX transfer builder ──────────────────────────────────────────────

    [Fact]
    public async Task Builds_a_native_trx_transfer_with_owner_to_and_amount_in_sun()
    {
        var txRpc = Substitute.For<ITronTxRpc>();
        txRpc.CreateTransactionAsync(Arg.Any<CreateTransactionRequest>(), Arg.Any<CancellationToken>())
            .Returns(TxJson("{\"txID\":\"cc\",\"raw_data_hex\":\"02\"}"));

        var unsigned = await new TronNativeTransferBuilder(txRpc, NullLogger<TronNativeTransferBuilder>.Instance)
            .BuildNativeTransferAsync(Chain.Tron, Staker, Deposit, new BigInteger(5_000_000), Ct);

        System.Text.Encoding.UTF8.GetString(unsigned.Payload).ShouldContain("\"txID\":\"cc\"");
        await txRpc.Received(1).CreateTransactionAsync(
            Arg.Is<CreateTransactionRequest>(r =>
                r.OwnerAddress == TronAddress.ToRawHex(Staker) && r.ToAddress == TronAddress.ToRawHex(Deposit) && r.Amount == 5_000_000),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_native_transfer_node_error_throws()
    {
        var txRpc = Substitute.For<ITronTxRpc>();
        var errorHex = Convert.ToHexString("balance is not sufficient"u8).ToLowerInvariant();
        txRpc.CreateTransactionAsync(Arg.Any<CreateTransactionRequest>(), Arg.Any<CancellationToken>())
            .Returns(TxJson($"{{\"Error\":\"{errorHex}\"}}"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await new TronNativeTransferBuilder(txRpc, NullLogger<TronNativeTransferBuilder>.Instance)
                .BuildNativeTransferAsync(Chain.Tron, Staker, Deposit, new BigInteger(5_000_000), Ct));
        ex.Message.ShouldContain("balance is not sufficient");
    }

    /// <summary>A detached <see cref="JsonElement"/> (survives its document's disposal) for the RPC fake.</summary>
    private static JsonElement TxJson(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ── Take-back, unstake, withdraw (step 5) ───────────────────────────────────

    [Fact]
    public async Task Builds_an_undelegate_that_takes_energy_back_from_the_receiver()
    {
        _rpc.UndelegateResourceAsync(Arg.Any<UndelegateResourceRequest>(), Arg.Any<CancellationToken>())
            .Returns(TxJson("{\"txID\":\"cc\",\"raw_data_hex\":\"02\"}"));

        await Builder.BuildUndelegateEnergyAsync(
            new UndelegateEnergyRequest(Chain.Tron, Staker, Deposit, new BigInteger(13_000_000_000)), Ct);

        await _rpc.Received(1).UndelegateResourceAsync(
            Arg.Is<UndelegateResourceRequest>(r =>
                r.OwnerAddress == TronAddress.ToRawHex(Staker) &&
                r.ReceiverAddress == TronAddress.ToRawHex(Deposit) &&
                r.Balance == 13_000_000_000 && r.Resource == "ENERGY"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Builds_an_unstake_and_a_withdraw_of_finished_unstakes()
    {
        _rpc.UnfreezeBalanceV2Async(Arg.Any<UnfreezeBalanceV2Request>(), Arg.Any<CancellationToken>())
            .Returns(TxJson("{\"txID\":\"dd\",\"raw_data_hex\":\"03\"}"));
        _rpc.WithdrawExpireUnfreezeAsync(Arg.Any<WithdrawExpireUnfreezeRequest>(), Arg.Any<CancellationToken>())
            .Returns(TxJson("{\"txID\":\"ee\",\"raw_data_hex\":\"04\"}"));

        await Builder.BuildUnstakeEnergyAsync(new UnstakeEnergyRequest(Chain.Tron, Staker, new BigInteger(5_000_000)), Ct);
        await Builder.BuildWithdrawUnstakedAsync(new WithdrawUnstakedRequest(Chain.Tron, Staker), Ct);

        await _rpc.Received(1).UnfreezeBalanceV2Async(
            Arg.Is<UnfreezeBalanceV2Request>(r =>
                r.OwnerAddress == TronAddress.ToRawHex(Staker) && r.UnfreezeBalance == 5_000_000 && r.Resource == "ENERGY"),
            Arg.Any<CancellationToken>());
        await _rpc.Received(1).WithdrawExpireUnfreezeAsync(
            Arg.Is<WithdrawExpireUnfreezeRequest>(r => r.OwnerAddress == TronAddress.ToRawHex(Staker)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_withdraw_with_nothing_finished_is_refused_by_the_node_and_throws()
    {
        // The node answers with an Error object when no unstake has finished its waiting period.
        _rpc.WithdrawExpireUnfreezeAsync(Arg.Any<WithdrawExpireUnfreezeRequest>(), Arg.Any<CancellationToken>())
            .Returns(TxJson("{\"Error\":\"636f6e7472616374\"}"));

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await Builder.BuildWithdrawUnstakedAsync(new WithdrawUnstakedRequest(Chain.Tron, Staker), Ct));
    }

    [Fact]
    public async Task The_amount_lent_is_read_from_the_chain_and_summed()
    {
        var dto = JsonSerializer.Deserialize<TronDelegatedResourceDto>(
            "{\"delegatedResource\":[{\"from\":\"41aa\",\"to\":\"41bb\",\"frozen_balance_for_energy\":13000000000}," +
            "{\"from\":\"41aa\",\"to\":\"41bb\",\"frozen_balance_for_energy\":2000000000}]}")!;
        _rpc.GetDelegatedResourceV2Async(TronAddress.ToRawHex(Staker), TronAddress.ToRawHex(Deposit), Arg.Any<CancellationToken>())
            .Returns(dto);

        var lent = await new TronAccountResourceReader(_rpc, TimeProvider.System)
            .GetDelegatedEnergySunAsync(Chain.Tron, Staker, Deposit, Ct);

        lent.ShouldBe(new BigInteger(15_000_000_000));
    }

    [Fact]
    public async Task Nothing_lent_reads_as_zero()
    {
        _rpc.GetDelegatedResourceV2Async(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Deserialize<TronDelegatedResourceDto>("{}")!);

        (await new TronAccountResourceReader(_rpc, TimeProvider.System)
            .GetDelegatedEnergySunAsync(Chain.Tron, Staker, Deposit, Ct)).ShouldBe(BigInteger.Zero);
    }

    [Fact]
    public async Task The_network_energy_rate_is_carried_on_the_snapshot()
    {
        _rpc.GetAccountResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Deserialize<TronAccountResourceDto>(
                "{\"TotalEnergyLimit\":180000000000,\"TotalEnergyWeight\":19000000000}")!);
        _rpc.GetAccountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new TronAccountDto());

        var snap = await new TronAccountResourceReader(_rpc, TimeProvider.System).GetAsync(Chain.Tron, Deposit, Ct);

        snap.NetworkTotalEnergyLimit.ShouldBe(new BigInteger(180_000_000_000));
        snap.NetworkTotalEnergyWeight.ShouldBe(new BigInteger(19_000_000_000));
    }
}
