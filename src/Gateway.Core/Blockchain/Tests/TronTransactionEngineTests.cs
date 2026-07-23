using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Tests;

/// <summary>
/// Level 2 — mock-JSON-RPC coverage for the real TRON money-out engine (builder + broadcaster). No live
/// node, no key: the ABI encoding, request shape, DTO mapping, error handling, idempotency, and retry are
/// all proven against canned TRON responses. The money-critical steps (recipient + exact amount encoding,
/// txID handling, success/revert classification) carry vectors.
/// </summary>
public sealed class TronTransactionEngineTests
{
    // Published USDT-TRC20 vector — its Base58 form and 20-byte EVM hash (independent, verifiable).
    private const string UsdtBase58 = "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t";
    private const string UsdtEvmHex = "a614f803b6fd780986a42c78ec9c7f77e6ded13c";
    private const string HotWallet = "TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH"; // a published BIP-44 vector address (sender)

    private static readonly Guid UsdtAssetId = Guid.CreateVersion7();
    private static AssetDto UsdtAsset() => new(UsdtAssetId, Chain.Tron, "USDT", UsdtBase58, 6, IsNative: false);

    private static BuildWithdrawalRequest Transfer(BigInteger amount) =>
        new(Chain.Tron, UsdtAssetId, HotWallet, UsdtBase58, amount);

    // ── ABI encoding: transfer(address,uint256) — money-critical, vectorized ──

    [Fact]
    public void EncodeTransfer_matches_the_abi_vector()
    {
        // 1 USDT = 1_000_000 base units = 0x0F4240.
        var parameter = TronAbi.EncodeTransfer(UsdtBase58, 1_000_000);

        parameter.ShouldBe(
            new string('0', 24) + UsdtEvmHex +   // recipient right-aligned in a 32-byte word
            new string('0', 58) + "0f4240");      // amount as 32-byte big-endian uint256
        parameter.Length.ShouldBe(128);          // two 32-byte words
    }

    [Fact]
    public void EncodeTransfer_encodes_max_uint256() =>
        TronAbi.EncodeTransfer(UsdtBase58, BigInteger.Pow(2, 256) - 1)
            .ShouldBe(new string('0', 24) + UsdtEvmHex + new string('f', 64));

    [Fact]
    public void EncodeTransfer_rejects_a_negative_amount() =>
        Should.Throw<ArgumentOutOfRangeException>(() => TronAbi.EncodeTransfer(UsdtBase58, BigInteger.MinusOne));

    [Fact]
    public void EncodeTransfer_rejects_an_amount_wider_than_uint256() =>
        Should.Throw<ArgumentOutOfRangeException>(() => TronAbi.EncodeTransfer(UsdtBase58, BigInteger.Pow(2, 256)));

    [Fact]
    public void ToRawHex_produces_the_21_byte_41_prefixed_form_and_round_trips()
    {
        TronAddress.ToRawHex(UsdtBase58).ShouldBe("41" + UsdtEvmHex);
        TronAddress.FromRawHex(TronAddress.ToRawHex(UsdtBase58)).ShouldBe(UsdtBase58);
    }

    // ── Builder ──

    private static TronTransactionBuilder Builder(FakeTronTxRpc rpc, AssetDto? asset) =>
        new(rpc, new FakeAssetCatalog(asset), new TronOptions { FeeLimitSun = 42_000_000 }, NullLogger<TronTransactionBuilder>.Instance);

    [Fact]
    public async Task Builder_triggers_the_encoded_transfer_and_returns_the_unsigned_tx()
    {
        const string txObject = """{"txID":"abc123","raw_data":{"expiration":1},"raw_data_hex":"0a02deadbeef"}""";
        var rpc = new FakeTronTxRpc
        {
            OnTrigger = _ => new TronTriggerResultDto
            {
                Result = new TronTriggerReturnDto { Result = true },
                Transaction = JsonDocument.Parse(txObject).RootElement.Clone(),
            },
        };

        var unsigned = await Builder(rpc, UsdtAsset()).BuildTransferAsync(Transfer(1_000_000), CancellationToken.None);

        rpc.LastTrigger.ShouldNotBeNull();
        rpc.LastTrigger!.OwnerAddress.ShouldBe(TronAddress.ToRawHex(HotWallet));
        rpc.LastTrigger.ContractAddress.ShouldBe(TronAddress.ToRawHex(UsdtBase58));
        rpc.LastTrigger.Parameter.ShouldBe(TronAbi.EncodeTransfer(UsdtBase58, 1_000_000));
        rpc.LastTrigger.FunctionSelector.ShouldBe("transfer(address,uint256)");
        rpc.LastTrigger.FeeLimit.ShouldBe(42_000_000);
        rpc.LastTrigger.CallValue.ShouldBe(0);
        rpc.LastTrigger.Visible.ShouldBeFalse();

        // The payload is the node's unsigned transaction object, carried opaquely to the signer.
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(unsigned.Payload));
        doc.RootElement.GetProperty("txID").GetString().ShouldBe("abc123");
    }

    [Fact]
    public async Task Builder_throws_with_the_decoded_reason_when_the_node_rejects_the_build()
    {
        var rpc = new FakeTronTxRpc
        {
            OnTrigger = _ => new TronTriggerResultDto
            {
                Result = new TronTriggerReturnDto
                {
                    Result = false,
                    Code = "CONTRACT_VALIDATE_ERROR",
                    Message = Convert.ToHexString(Encoding.UTF8.GetBytes("balance is not sufficient")),
                },
            },
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Builder(rpc, UsdtAsset()).BuildTransferAsync(Transfer(1_000_000), CancellationToken.None));

        ex.Message.ShouldContain("CONTRACT_VALIDATE_ERROR");
        ex.Message.ShouldContain("balance is not sufficient"); // hex-decoded
    }

    [Fact]
    public async Task Builder_rejects_an_unknown_asset() =>
        await Should.ThrowAsync<InvalidOperationException>(
            () => Builder(new FakeTronTxRpc(), asset: null).BuildTransferAsync(Transfer(1_000_000), CancellationToken.None));

    [Fact]
    public async Task Builder_rejects_a_native_trx_asset()
    {
        var native = new AssetDto(UsdtAssetId, Chain.Tron, "TRX", null, 6, IsNative: true);
        await Should.ThrowAsync<NotSupportedException>(
            () => Builder(new FakeTronTxRpc(), native).BuildTransferAsync(Transfer(1_000_000), CancellationToken.None));
    }

    // ── Broadcaster ──

    private static TronTransactionBroadcaster Broadcaster(FakeTronTxRpc rpc) =>
        new(rpc, NullLogger<TronTransactionBroadcaster>.Instance);

    private static byte[] SignedBlob(string txId) =>
        Encoding.UTF8.GetBytes($$"""{"txID":"{{txId}}","signature":["deadbeef"],"raw_data_hex":"0a02"}""");

    [Fact]
    public async Task Broadcast_success_returns_the_txid_from_the_signed_blob()
    {
        var rpc = new FakeTronTxRpc { OnBroadcast = _ => new TronBroadcastResultDto { Result = true, Txid = "server-echoed" } };

        var result = await Broadcaster(rpc).BroadcastAsync(Chain.Tron, SignedBlob("abc123"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TransactionHash.ShouldBe("abc123"); // the tx's own txID, not the server echo
    }

    [Fact]
    public async Task Broadcast_treats_a_duplicate_as_success_idempotent()
    {
        // Re-broadcasting the same signed blob (crash-retry) — the network already has it.
        var rpc = new FakeTronTxRpc { OnBroadcast = _ => new TronBroadcastResultDto { Result = false, Code = "DUP_TRANSACTION_ERROR" } };

        var result = await Broadcaster(rpc).BroadcastAsync(Chain.Tron, SignedBlob("abc123"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TransactionHash.ShouldBe("abc123");
    }

    [Fact]
    public async Task Broadcast_failure_maps_the_code_and_decoded_message()
    {
        var rpc = new FakeTronTxRpc
        {
            OnBroadcast = _ => new TronBroadcastResultDto
            {
                Result = false,
                Code = "SIGERROR",
                Message = Convert.ToHexString(Encoding.UTF8.GetBytes("validate signature error")),
            },
        };

        var result = await Broadcaster(rpc).BroadcastAsync(Chain.Tron, SignedBlob("abc123"), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("broadcast.SIGERROR");
        result.Error.Message.ShouldBe("validate signature error");
    }

    [Fact]
    public async Task Broadcast_rejects_a_signed_blob_without_a_txid()
    {
        var rpc = new FakeTronTxRpc { OnBroadcast = _ => new TronBroadcastResultDto { Result = true } };

        var result = await Broadcaster(rpc).BroadcastAsync(
            Chain.Tron, Encoding.UTF8.GetBytes("""{"signature":["x"]}"""), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("broadcast.malformed");
    }

    [Fact]
    public async Task Broadcast_rejects_a_non_json_signed_blob()
    {
        var rpc = new FakeTronTxRpc();
        var result = await Broadcaster(rpc).BroadcastAsync(Chain.Tron, "not json"u8.ToArray(), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("broadcast.malformed");
    }

    // ── Status classification ──

    [Fact]
    public async Task Status_is_null_when_not_yet_mined()
    {
        var rpc = new FakeTronTxRpc { OnGetInfo = _ => null };
        (await Broadcaster(rpc).GetTransactionStatusAsync(Chain.Tron, "abc", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Status_is_null_when_the_info_has_no_block_yet()
    {
        var rpc = new FakeTronTxRpc { OnGetInfo = _ => new TronTransactionInfoDto { Id = "abc", BlockNumber = null } };
        (await Broadcaster(rpc).GetTransactionStatusAsync(Chain.Tron, "abc", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Status_is_succeeded_for_a_mined_SUCCESS_receipt()
    {
        var rpc = new FakeTronTxRpc
        {
            OnGetInfo = _ => new TronTransactionInfoDto { Id = "abc", BlockNumber = 100, Receipt = new TronReceiptDto { Result = "SUCCESS" } },
        };

        var status = await Broadcaster(rpc).GetTransactionStatusAsync(Chain.Tron, "abc", CancellationToken.None);

        status.ShouldNotBeNull();
        status!.BlockNumber.ShouldBe(100);
        status.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Status_is_not_succeeded_for_a_reverted_receipt()
    {
        // Mined but reverted → block present, Succeeded=false → the withdrawal is held for ops, never settled.
        var rpc = new FakeTronTxRpc
        {
            OnGetInfo = _ => new TronTransactionInfoDto { Id = "abc", BlockNumber = 100, Receipt = new TronReceiptDto { Result = "REVERT" } },
        };

        var status = await Broadcaster(rpc).GetTransactionStatusAsync(Chain.Tron, "abc", CancellationToken.None);

        status.ShouldNotBeNull();
        status!.Succeeded.ShouldBeFalse();
    }

    // ── DTO deserialization (raw node JSON) ──

    [Fact]
    public void A_triggersmartcontract_result_deserializes()
    {
        const string json = """
        {
          "result": { "result": true },
          "transaction": { "txID": "9a1b", "raw_data": { "expiration": 1 }, "raw_data_hex": "0a02" }
        }
        """;

        var dto = JsonSerializer.Deserialize<TronTriggerResultDto>(json)!;

        dto.Result!.Result.ShouldBeTrue();
        dto.Transaction.ShouldNotBeNull();
        dto.Transaction!.Value.GetProperty("txID").GetString().ShouldBe("9a1b");
    }

    [Fact]
    public void A_broadcast_failure_deserializes_with_code_and_hex_message()
    {
        const string json = """{ "result": false, "code": "DUP_TRANSACTION_ERROR", "message": "6475706c6963617465" }""";

        var dto = JsonSerializer.Deserialize<TronBroadcastResultDto>(json)!;

        dto.Result.ShouldBeFalse();
        dto.Code.ShouldBe("DUP_TRANSACTION_ERROR");
        TronErrorMessage.Decode(dto.Message).ShouldBe("duplicate");
    }

    [Fact]
    public void A_gettransactioninfobyid_result_deserializes()
    {
        const string json = """{ "id": "9a1b", "blockNumber": 12345, "receipt": { "result": "SUCCESS" } }""";

        var dto = JsonSerializer.Deserialize<TronTransactionInfoDto>(json)!;

        dto.BlockNumber.ShouldBe(12345);
        dto.Receipt!.Result.ShouldBe("SUCCESS");
    }

    [Fact]
    public void Plain_text_error_messages_pass_through_undecoded() =>
        TronErrorMessage.Decode("Transaction expired").ShouldBe("Transaction expired");

    // ── Transport: TronRpc over a mock HttpMessageHandler (parsing, empty→null, error, retry) ──

    private static TronRpc RpcOver(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://node.example/") });

    [Fact]
    public async Task TronRpc_parses_transaction_info_and_maps_empty_to_null()
    {
        var mined = RpcOver(new StubHandler(_ => (HttpStatusCode.OK, """{ "id":"9a","blockNumber":7,"receipt":{"result":"SUCCESS"} }""")));
        var info = await mined.GetTransactionInfoAsync("9a", CancellationToken.None);
        info.ShouldNotBeNull();
        info!.BlockNumber.ShouldBe(7);

        var pending = RpcOver(new StubHandler(_ => (HttpStatusCode.OK, "{}")));
        (await pending.GetTransactionInfoAsync("9a", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task TronRpc_parses_a_broadcast_failure_body()
    {
        var rpc = RpcOver(new StubHandler(_ => (HttpStatusCode.OK, """{ "result": false, "code": "SIGERROR" }""")));
        using var tx = JsonDocument.Parse("""{"txID":"9a"}""");

        var dto = await rpc.BroadcastTransactionAsync(tx.RootElement, CancellationToken.None);

        dto.Result.ShouldBeFalse();
        dto.Code.ShouldBe("SIGERROR");
    }

    [Fact]
    public async Task TronRpc_retries_transient_failures()
    {
        // 503 twice, then 200 — the standard resilience pipeline must retry through to success.
        var counting = new CountingHandler(failCount: 2, okBody: """{ "id":"9a","blockNumber":7,"receipt":{"result":"SUCCESS"} }""");

        var services = new ServiceCollection();
        services.AddHttpClient<ITronTxRpc, TronRpc>(c => c.BaseAddress = new Uri("https://node.example/"))
            .ConfigurePrimaryHttpMessageHandler(() => counting)
            .AddStandardResilienceHandler(o =>
            {
                o.Retry.MaxRetryAttempts = 3;
                o.Retry.Delay = TimeSpan.FromMilliseconds(1);
                o.Retry.BackoffType = DelayBackoffType.Constant;
                o.Retry.UseJitter = false;
            });

        await using var provider = services.BuildServiceProvider();
        var rpc = provider.GetRequiredService<ITronTxRpc>();

        var info = await rpc.GetTransactionInfoAsync("9a", CancellationToken.None);

        info.ShouldNotBeNull();
        info!.BlockNumber.ShouldBe(7);
        counting.Calls.ShouldBe(3); // two 503s retried, third OK
    }

    // ── DI wiring ──

    [Fact]
    public void AddTronTransactionEngine_registers_the_tron_builder_and_broadcaster()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Chains:Tron:RpcBaseUrl"] = "https://node.example" })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAssetCatalog>(new FakeAssetCatalog(UsdtAsset()));
        services.AddTronTransactionEngine(config);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITransactionBuilder>().ShouldBeOfType<TronTransactionBuilder>();
        scope.ServiceProvider.GetRequiredService<ITransactionBroadcaster>().ShouldBeOfType<TronTransactionBroadcaster>();
    }

    // ── Fakes ──

    private sealed class FakeTronTxRpc : ITronTxRpc
    {
        public TriggerSmartContractRequest? LastTrigger { get; private set; }
        public Func<TriggerSmartContractRequest, TronTriggerResultDto> OnTrigger { get; init; } = _ => throw new NotImplementedException();
        public Func<JsonElement, TronBroadcastResultDto> OnBroadcast { get; init; } = _ => throw new NotImplementedException();
        public Func<string, TronTransactionInfoDto?> OnGetInfo { get; init; } = _ => null;

        public Task<TronTriggerResultDto> TriggerSmartContractAsync(TriggerSmartContractRequest request, CancellationToken ct = default)
        {
            LastTrigger = request;
            return Task.FromResult(OnTrigger(request));
        }

        public Task<TronBroadcastResultDto> BroadcastTransactionAsync(JsonElement signedTransaction, CancellationToken ct = default) =>
            Task.FromResult(OnBroadcast(signedTransaction));

        public Task<TronTransactionInfoDto?> GetTransactionInfoAsync(string transactionId, CancellationToken ct = default) =>
            Task.FromResult(OnGetInfo(transactionId));
    }

    private sealed class FakeAssetCatalog(AssetDto? asset) : IAssetCatalog
    {
        public Task<AssetDto?> FindByIdAsync(Guid assetId, CancellationToken ct = default) => Task.FromResult(asset);
        public Task<AssetDto?> FindAsync(Chain chain, string symbol, CancellationToken ct = default) => Task.FromResult<AssetDto?>(null);
        public Task<IReadOnlyList<AssetDto>> GetActiveAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AssetDto>>(asset is null ? [] : [asset]);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = respond(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class CountingHandler(int failCount, string okBody) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var ok = Calls > failCount;
            return Task.FromResult(new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(ok ? okBody : "{}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
