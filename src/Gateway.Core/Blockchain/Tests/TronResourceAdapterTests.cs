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

    /// <summary>A detached <see cref="JsonElement"/> (survives its document's disposal) for the RPC fake.</summary>
    private static JsonElement TxJson(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
