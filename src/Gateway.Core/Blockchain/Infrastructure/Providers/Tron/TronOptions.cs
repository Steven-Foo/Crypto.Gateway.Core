namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;

/// <summary>TRON node connection settings, bound from config <c>Chains:Tron</c>.</summary>
public sealed class TronOptions
{
    public const string SectionName = "Chains:Tron";

    /// <summary>TronGrid / full-node base URL (the adapter appends <c>jsonrpc</c> and <c>walletsolidity/…</c>).</summary>
    public string RpcBaseUrl { get; set; } = "https://api.trongrid.io";

    /// <summary>Optional TronGrid API key, sent as the <c>TRON-PRO-API-KEY</c> header. Not a secret worth hiding in logs, but never logged anyway.</summary>
    public string? ApiKey { get; set; }
}
