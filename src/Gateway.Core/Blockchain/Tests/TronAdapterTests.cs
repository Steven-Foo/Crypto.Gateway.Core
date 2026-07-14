using System.Numerics;
using System.Text.Json;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Rpc;
using CryptoPaymentEngine.SharedKernel;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Tests;

public sealed class TronAdapterTests
{
    // The canonical USDT-TRC20 contract and its 20-byte EVM-hex form — an independent, published vector.
    private const string UsdtBase58 = "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t";
    private const string UsdtEvmHex = "a614f803b6fd780986a42c78ec9c7f77e6ded13c";

    // ── Address conversion (money-critical: a wrong recipient credits the wrong merchant) ──

    [Fact]
    public void Evm_hex_maps_to_the_published_TRON_address()
    {
        TronAddress.FromEvmHex(UsdtEvmHex).ShouldBe(UsdtBase58);
        TronAddress.FromEvmHex("0x" + UsdtEvmHex).ShouldBe(UsdtBase58);
        TronAddress.ToEvmHex(UsdtBase58).ShouldBe(UsdtEvmHex);
    }

    [Fact]
    public void Address_conversion_round_trips_a_derived_deposit_address()
    {
        // A published BIP-44 vector address from the wallet tests; round-trip through both trusted paths.
        const string deposit = "TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH";
        TronAddress.FromEvmHex(TronAddress.ToEvmHex(deposit)).ShouldBe(deposit);
    }

    [Fact]
    public void An_abi_padded_topic_decodes_to_the_address()
    {
        var hex = TronAddress.ToEvmHex(UsdtBase58);
        var topic = "0x" + new string('0', 24) + hex; // 32-byte left-padded
        TronAddress.FromEvmTopic(topic).ShouldBe(UsdtBase58);
    }

    // ── Hex quantity parsing (exact base units, never double) ──

    [Theory]
    [InlineData("0xf4240", "1000000")]                       // 1 USDT (6 dp)
    [InlineData("0x0de0b6b3a7640000", "1000000000000000000")] // 1e18
    [InlineData("0x0", "0")]
    [InlineData("0x", "0")]
    public void Hex_amount_parses_to_exact_base_units(string hex, string expected) =>
        HexNumber.ToBigInteger(hex).ShouldBe(BigInteger.Parse(expected));

    [Fact]
    public void A_high_bit_hex_value_is_read_as_unsigned() =>
        HexNumber.ToBigInteger("0xffffffffffffffffffffffffffffffff").ShouldBe(BigInteger.Pow(2, 128) - 1);

    // ── Transfer log → DetectedTransfer mapping ──

    private static readonly Guid UsdtAssetId = Guid.CreateVersion7();
    private static readonly string RecipientHex = TronAddress.ToEvmHex("TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH");

    private static IReadOnlyDictionary<string, Guid> KnownContracts() =>
        new Dictionary<string, Guid> { [UsdtEvmHex] = UsdtAssetId };

    private static TronLogDto TransferLog(string amountHex, string topic0 = TronConstants.TransferEventSignature) => new()
    {
        Address = "0x" + UsdtEvmHex,
        Topics =
        [
            topic0,
            "0x" + new string('0', 24) + TronAddress.ToEvmHex("TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t"), // from
            "0x" + new string('0', 24) + RecipientHex,                                                 // to
        ],
        Data = amountHex,
        BlockNumber = "0x64", // 100
        BlockHash = "0xblockhash",
        TransactionHash = "0xdeadbeef",
        LogIndex = "0x3",
    };

    [Fact]
    public void A_usdt_transfer_maps_to_the_recipient_asset_and_exact_amount()
    {
        TronChainAdapter.TryMapTransfer(TransferLog("0xf4240"), KnownContracts(), out var transfer).ShouldBeTrue();

        transfer.Chain.ShouldBe(Chain.Tron);
        transfer.Address.ShouldBe("TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH");
        transfer.AssetId.ShouldBe(UsdtAssetId);
        transfer.Amount.ShouldBe(new BigInteger(1_000_000));
        transfer.TransactionHash.ShouldBe("0xdeadbeef");
        transfer.OutputIndex.ShouldBe(3);
        transfer.BlockNumber.ShouldBe(100);
        transfer.BlockHash.ShouldBe("0xblockhash");
    }

    [Fact]
    public void A_non_transfer_log_is_ignored() =>
        TronChainAdapter.TryMapTransfer(TransferLog("0xf4240", topic0: "0xdeadbeef"), KnownContracts(), out _).ShouldBeFalse();

    [Fact]
    public void A_transfer_from_an_unknown_contract_is_ignored() =>
        TronChainAdapter.TryMapTransfer(TransferLog("0xf4240"), new Dictionary<string, Guid>(), out _).ShouldBeFalse();

    [Fact]
    public void A_zero_amount_transfer_is_ignored() =>
        TronChainAdapter.TryMapTransfer(TransferLog("0x0"), KnownContracts(), out _).ShouldBeFalse();

    // ── DTO shape: an eth_getLogs result deserializes correctly ──

    [Fact]
    public void An_eth_getLogs_result_deserializes_into_the_dto()
    {
        const string json = """
        [{
          "address": "0xa614f803b6fd780986a42c78ec9c7f77e6ded13c",
          "topics": ["0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef",
                     "0x0000000000000000000000001111111111111111111111111111111111111111",
                     "0x0000000000000000000000002222222222222222222222222222222222222222"],
          "data": "0xf4240",
          "blockNumber": "0x64",
          "blockHash": "0xabc",
          "transactionHash": "0xtx",
          "logIndex": "0x2"
        }]
        """;

        var logs = JsonSerializer.Deserialize<List<TronLogDto>>(json)!;

        logs.Count.ShouldBe(1);
        logs[0].Address.ShouldBe("0xa614f803b6fd780986a42c78ec9c7f77e6ded13c");
        logs[0].Topics.Length.ShouldBe(3);
        logs[0].Data.ShouldBe("0xf4240");
        logs[0].BlockNumber.ShouldBe("0x64");
        logs[0].TransactionHash.ShouldBe("0xtx");
        logs[0].LogIndex.ShouldBe("0x2");
    }
}
