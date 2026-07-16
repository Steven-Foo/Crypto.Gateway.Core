using System.Numerics;
using System.Text.Json;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Rpc;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
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

    // ── Native TRX (TransferContract) detection ──

    // The recipient's raw, already-0x41-prefixed hex form as TRON's native wallet API returns it
    // (owner_address/to_address) — distinct from the 20-byte unprefixed EVM-hex the eth-compatible RPC uses.
    private const string RecipientRawHex = "41" + "a614f803b6fd780986a42c78ec9c7f77e6ded13c";

    [Fact]
    public void Raw_hex_maps_to_the_published_TRON_address() =>
        TronAddress.FromRawHex(RecipientRawHex).ShouldBe(UsdtBase58);

    [Fact]
    public void Raw_hex_without_the_0x41_prefix_is_rejected() =>
        Should.Throw<FormatException>(() => TronAddress.FromRawHex(UsdtEvmHex));

    private static readonly Guid TrxAssetId = Guid.CreateVersion7();

    private static TronNativeBlockDto NativeBlock(params TronNativeTransactionDto[] transactions) => new()
    {
        BlockId = "0xnativeblockhash",
        BlockHeader = new TronBlockHeaderDto { RawData = new TronBlockHeaderRawDataDto { Number = 200 } },
        Transactions = [.. transactions],
    };

    private static TronNativeTransactionDto TransferTx(
        string txId, long amountSun, string? contractRet = TronConstants.ContractRetSuccess, string toRawHex = RecipientRawHex) => new()
    {
        TxId = txId,
        Ret = contractRet is null ? [] : [new TronTransactionRetDto { ContractRet = contractRet }],
        RawData = new TronNativeRawDataDto
        {
            Contract =
            [
                new TronNativeContractDto
                {
                    Type = TronConstants.TransferContractType,
                    Parameter = new TronContractParameterDto
                    {
                        Value = new TronTransferContractValueDto { Amount = amountSun, ToAddress = toRawHex, OwnerAddress = "41" + new string('0', 40) },
                    },
                },
            ],
        },
    };

    [Fact]
    public void A_successful_native_transfer_maps_to_the_recipient_asset_and_exact_amount()
    {
        var block = NativeBlock(TransferTx("0xnativetx", 5_000_000));

        var transfers = TronChainAdapter.ExtractNativeTransfers(block, TrxAssetId).ToList();

        transfers.Count.ShouldBe(1);
        var transfer = transfers[0];
        transfer.Chain.ShouldBe(Chain.Tron);
        transfer.Address.ShouldBe(UsdtBase58);
        transfer.AssetId.ShouldBe(TrxAssetId);
        transfer.Amount.ShouldBe(new BigInteger(5_000_000));
        transfer.TransactionHash.ShouldBe("0xnativetx");
        transfer.OutputIndex.ShouldBe(0);
        transfer.BlockNumber.ShouldBe(200);
        transfer.BlockHash.ShouldBe("0xnativeblockhash");
    }

    [Fact]
    public void A_failed_transaction_is_ignored()
    {
        var block = NativeBlock(TransferTx("0xfailedtx", 5_000_000, contractRet: "REVERT"));
        TronChainAdapter.ExtractNativeTransfers(block, TrxAssetId).ShouldBeEmpty();
    }

    [Fact]
    public void A_zero_amount_native_transfer_is_ignored()
    {
        var block = NativeBlock(TransferTx("0xzerotx", 0));
        TronChainAdapter.ExtractNativeTransfers(block, TrxAssetId).ShouldBeEmpty();
    }

    [Fact]
    public void A_non_transfer_contract_type_is_ignored()
    {
        var tx = TransferTx("0xothertx", 5_000_000);
        tx.RawData!.Contract[0] = tx.RawData.Contract[0] with { Type = "AccountPermissionUpdateContract" };
        var block = NativeBlock(tx);

        TronChainAdapter.ExtractNativeTransfers(block, TrxAssetId).ShouldBeEmpty();
    }

    [Fact]
    public void Multiple_contracts_in_one_transaction_use_the_contract_index_as_output_index()
    {
        var tx = TransferTx("0xmultitx", 1_000_000);
        tx.RawData!.Contract.Add(tx.RawData.Contract[0] with
        {
            Parameter = new TronContractParameterDto
            {
                Value = new TronTransferContractValueDto { Amount = 2_000_000, ToAddress = RecipientRawHex, OwnerAddress = "41" + new string('0', 40) },
            },
        });
        var block = NativeBlock(tx);

        var transfers = TronChainAdapter.ExtractNativeTransfers(block, TrxAssetId).ToList();

        transfers.Count.ShouldBe(2);
        transfers[0].OutputIndex.ShouldBe(0);
        transfers[1].OutputIndex.ShouldBe(1);
    }

    [Fact]
    public async Task ScanAsync_stamps_native_transfers_with_the_eth_compatible_hash_not_the_native_blockID()
    {
        // The native and eth-compatible APIs report DIFFERENT hash strings for the identical block — this
        // is the exact real-world shape that made every native deposit look permanently reorged, because
        // DepositConfirmationService's canonicality check reads the eth-compatible hash via GetBlockAsync.
        const string nativeBlockId = "0000000004203e0e096aa405defc279bbb51a7469d063a0fdac0c2884b743d70";
        const string ethHash = "0x000000000420380e5150b77c447264e99e462c9bdae798112fa83b3d18a02de5";

        var block = NativeBlock(TransferTx("0xrealtx", 1_000_000)) with { BlockId = nativeBlockId };

        var rpc = new FakeTronRpc(
            nativeBlocks: [block],
            ethBlocksByNumber: new Dictionary<long, TronBlockDto> { [200] = new() { Number = "0xc8", Hash = ethHash } });

        var catalog = new FakeAssetCatalog(TrxAssetId);
        var adapter = new TronChainAdapter(rpc, catalog, NullLogger<TronChainAdapter>.Instance);

        var transfers = await adapter.ScanAsync(Chain.Tron, 200, 200, CancellationToken.None);

        transfers.Count.ShouldBe(1);
        transfers[0].BlockHash.ShouldBe(ethHash);
        transfers[0].BlockHash.ShouldNotBe(nativeBlockId);
    }

    private sealed class FakeAssetCatalog(Guid nativeAssetId) : IAssetCatalog
    {
        public Task<AssetDto?> FindByIdAsync(Guid assetId, CancellationToken ct = default) =>
            Task.FromResult<AssetDto?>(null);

        public Task<AssetDto?> FindAsync(Chain chain, string symbol, CancellationToken ct = default) =>
            Task.FromResult<AssetDto?>(null);

        public Task<IReadOnlyList<AssetDto>> GetActiveAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AssetDto>>([new AssetDto(nativeAssetId, Chain.Tron, "TRX", null, 6, IsNative: true)]);
    }

    private sealed class FakeTronRpc(IReadOnlyList<TronNativeBlockDto> nativeBlocks, IReadOnlyDictionary<long, TronBlockDto> ethBlocksByNumber) : ITronRpc
    {
        public Task<long> GetBlockNumberAsync(CancellationToken ct = default) => Task.FromResult(0L);

        public Task<TronBlockDto?> GetBlockByNumberAsync(long blockNumber, CancellationToken ct = default) =>
            Task.FromResult(ethBlocksByNumber.GetValueOrDefault(blockNumber));

        public Task<IReadOnlyList<TronLogDto>> GetTransferLogsAsync(
            long fromBlock, long toBlock, IReadOnlyCollection<string> contractHexAddresses, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TronLogDto>>([]);

        public Task<long> GetSolidifiedBlockNumberAsync(CancellationToken ct = default) => Task.FromResult(0L);

        public Task<IReadOnlyList<TronNativeBlockDto>> GetBlockRangeAsync(long fromBlock, long toBlock, CancellationToken ct = default) =>
            Task.FromResult(nativeBlocks);
    }

    [Fact]
    public void A_getblockbylimitnext_result_deserializes_into_the_dto()
    {
        const string json = """
        {
          "block": [{
            "blockID": "00000000...deadbeef",
            "block_header": { "raw_data": { "number": 12345 } },
            "transactions": [{
              "txID": "abc123",
              "ret": [{ "contractRet": "SUCCESS" }],
              "raw_data": {
                "contract": [{
                  "type": "TransferContract",
                  "parameter": {
                    "value": { "amount": 1000000, "owner_address": "41aaaa...", "to_address": "41bbbb..." }
                  }
                }]
              }
            }]
          }]
        }
        """;

        var response = JsonSerializer.Deserialize<TronBlockRangeResponseDto>(json)!;

        response.Block.Count.ShouldBe(1);
        response.Block[0].BlockHeader!.RawData.Number.ShouldBe(12345L);
        response.Block[0].Transactions.Count.ShouldBe(1);
        var tx = response.Block[0].Transactions[0];
        tx.TxId.ShouldBe("abc123");
        tx.Ret[0].ContractRet.ShouldBe("SUCCESS");
        tx.RawData!.Contract[0].Type.ShouldBe("TransferContract");
        tx.RawData.Contract[0].Parameter!.Value!.Amount.ShouldBe(1000000L);
    }
}
