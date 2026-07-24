using System.Text.Json;
using System.Text.Json.Serialization;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;

/// <summary>A log entry from TRON's Ethereum-compatible <c>eth_getLogs</c>. All quantities are 0x-hex.</summary>
public sealed record TronLogDto
{
    [JsonPropertyName("address")] public string Address { get; init; } = "";
    [JsonPropertyName("topics")] public string[] Topics { get; init; } = [];
    [JsonPropertyName("data")] public string Data { get; init; } = "";
    [JsonPropertyName("blockNumber")] public string BlockNumber { get; init; } = "";
    [JsonPropertyName("blockHash")] public string BlockHash { get; init; } = "";
    [JsonPropertyName("transactionHash")] public string TransactionHash { get; init; } = "";
    [JsonPropertyName("logIndex")] public string LogIndex { get; init; } = "";
}

/// <summary>A block header from <c>eth_getBlockByNumber</c> (only the fields we need).</summary>
public sealed record TronBlockDto
{
    [JsonPropertyName("number")] public string Number { get; init; } = "";
    [JsonPropertyName("hash")] public string Hash { get; init; } = "";
}

public static class TronConstants
{
    /// <summary>keccak256("Transfer(address,address,uint256)") — the TRC-20/ERC-20 transfer topic0.</summary>
    public const string TransferEventSignature = "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";

    /// <summary>The native-transaction contract type for a plain TRX value transfer (no smart contract).</summary>
    public const string TransferContractType = "TransferContract";

    /// <summary>The <c>ret[].contractRet</c> value marking a transaction as having actually executed.</summary>
    public const string ContractRetSuccess = "SUCCESS";
}

/// <summary>Response shape of <c>/wallet/getblockbylimitnext</c> (native, non-eth-compatible API).</summary>
public sealed record TronBlockRangeResponseDto
{
    [JsonPropertyName("block")] public List<TronNativeBlockDto> Block { get; init; } = [];
}

/// <summary>One block as returned by the native wallet API, including its full transaction list.</summary>
public sealed record TronNativeBlockDto
{
    [JsonPropertyName("blockID")] public string BlockId { get; init; } = "";
    [JsonPropertyName("block_header")] public TronBlockHeaderDto? BlockHeader { get; init; }
    [JsonPropertyName("transactions")] public List<TronNativeTransactionDto> Transactions { get; init; } = [];
}

public sealed record TronBlockHeaderDto
{
    [JsonPropertyName("raw_data")] public TronBlockHeaderRawDataDto RawData { get; init; } = new();
}

public sealed record TronBlockHeaderRawDataDto
{
    [JsonPropertyName("number")] public long Number { get; init; }
}

public sealed record TronNativeTransactionDto
{
    [JsonPropertyName("txID")] public string TxId { get; init; } = "";
    [JsonPropertyName("ret")] public List<TronTransactionRetDto> Ret { get; init; } = [];
    [JsonPropertyName("raw_data")] public TronNativeRawDataDto? RawData { get; init; }
}

public sealed record TronTransactionRetDto
{
    [JsonPropertyName("contractRet")] public string? ContractRet { get; init; }
}

public sealed record TronNativeRawDataDto
{
    [JsonPropertyName("contract")] public List<TronNativeContractDto> Contract { get; init; } = [];
}

public sealed record TronNativeContractDto
{
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("parameter")] public TronContractParameterDto? Parameter { get; init; }
}

public sealed record TronContractParameterDto
{
    [JsonPropertyName("value")] public TronTransferContractValueDto? Value { get; init; }
}

/// <summary>The decoded <c>TransferContract</c> payload: a plain TRX value transfer. Amounts are in sun.</summary>
public sealed record TronTransferContractValueDto
{
    [JsonPropertyName("amount")] public long Amount { get; init; }
    [JsonPropertyName("owner_address")] public string? OwnerAddress { get; init; }
    [JsonPropertyName("to_address")] public string? ToAddress { get; init; }
}

// ── Write path: build / broadcast / status (native /wallet/* API, NOT the eth jsonrpc envelope) ──
// None of these carries a key (§10): triggersmartcontract builds an UNSIGNED tx, broadcasttransaction
// sends an ALREADY-SIGNED blob, gettransactioninfobyid only reads status.

/// <summary>
/// Request body for <c>/wallet/triggersmartcontract</c> — builds an unsigned TRC-20 <c>transfer</c>.
/// Addresses are the 21-byte <c>41…</c> hex form (<c>visible=false</c>); <see cref="Parameter"/> is the
/// ABI-encoded <c>(to, amount)</c> from <see cref="TronAbi.EncodeTransfer"/>.
/// </summary>
public sealed record TriggerSmartContractRequest
{
    [JsonPropertyName("owner_address")] public required string OwnerAddress { get; init; }
    [JsonPropertyName("contract_address")] public required string ContractAddress { get; init; }
    [JsonPropertyName("function_selector")] public string FunctionSelector { get; init; } = "transfer(address,uint256)";
    [JsonPropertyName("parameter")] public required string Parameter { get; init; }
    [JsonPropertyName("fee_limit")] public long FeeLimit { get; init; }
    [JsonPropertyName("call_value")] public long CallValue { get; init; }
    [JsonPropertyName("visible")] public bool Visible { get; init; }
}

/// <summary>
/// Response of <c>/wallet/triggersmartcontract</c>. <see cref="Result"/> reports whether the node accepted
/// the build; <see cref="Transaction"/> is the raw unsigned transaction object (with <c>txID</c> /
/// <c>raw_data</c> / <c>raw_data_hex</c>) carried opaquely to the signer and broadcaster.
/// </summary>
public sealed record TronTriggerResultDto
{
    [JsonPropertyName("result")] public TronTriggerReturnDto? Result { get; init; }
    [JsonPropertyName("transaction")] public JsonElement? Transaction { get; init; }
}

/// <summary>The <c>result</c> object of a build: <c>{ "result": true }</c> or <c>{ "code", "message" }</c>.</summary>
public sealed record TronTriggerReturnDto
{
    [JsonPropertyName("result")] public bool Result { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
}

/// <summary>
/// Response of <c>/wallet/broadcasttransaction</c>. Here <c>result</c> is a top-level bool (unlike the
/// build, whose <c>result</c> is an object). <see cref="Message"/> is hex-encoded ASCII on failure.
/// </summary>
public sealed record TronBroadcastResultDto
{
    [JsonPropertyName("result")] public bool Result { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("txid")] public string? Txid { get; init; }
}

/// <summary>
/// Response of <c>/wallet/gettransactioninfobyid</c>. An empty <c>{}</c> means the tx is not yet mined
/// (or unknown/dropped). For a smart-contract call, <c>receipt.result == "SUCCESS"</c> is the success test.
/// </summary>
public sealed record TronTransactionInfoDto
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("blockNumber")] public long? BlockNumber { get; init; }
    [JsonPropertyName("receipt")] public TronReceiptDto? Receipt { get; init; }
}

public sealed record TronReceiptDto
{
    [JsonPropertyName("result")] public string? Result { get; init; }
}
