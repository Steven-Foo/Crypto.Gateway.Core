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
