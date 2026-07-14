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
}
