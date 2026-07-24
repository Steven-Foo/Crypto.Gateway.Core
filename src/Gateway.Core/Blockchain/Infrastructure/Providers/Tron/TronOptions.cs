namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;

/// <summary>TRON node connection settings, bound from config <c>Chains:Tron</c>.</summary>
public sealed class TronOptions
{
    public const string SectionName = "Chains:Tron";

    /// <summary>TronGrid / full-node base URL (the adapter appends <c>jsonrpc</c> and <c>walletsolidity/…</c>).</summary>
    public string RpcBaseUrl { get; set; } = "https://api.trongrid.io";

    /// <summary>Optional TronGrid API key, sent as the <c>TRON-PRO-API-KEY</c> header. Not a secret worth hiding in logs, but never logged anyway.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Fee ceiling in sun for a smart-contract call (the max energy/bandwidth the node may burn building
    /// the transaction). A TRC-20 transfer needs far less; this is a safety cap, not the actual cost.
    /// Default 100 TRX (100_000_000 sun).
    /// </summary>
    public long FeeLimitSun { get; set; } = 100_000_000;
}
