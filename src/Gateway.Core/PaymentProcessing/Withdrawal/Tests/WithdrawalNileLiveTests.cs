using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Signing;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Tests;

/// <summary>
/// Level 3, live proof: a REAL TRC-20 transfer built, signed, broadcast, and confirmed on the TRON <b>Nile
/// testnet</b> through the same production adapters. Gated on <c>CPE_NILE_*</c> environment variables so it is
/// skipped in CI and normal runs — it needs a funded throwaway Nile account and network access to a Nile node.
/// It moves faucet-only test funds; never point it at a mainnet endpoint or a key holding real value.
///
/// Run it (PowerShell), after funding the throwaway account with test TRX + test USDT (see
/// <c>docs/withdrawal-testnet.md</c>):
/// <code>
/// $env:CPE_NILE_RPC     = "https://nile.trongrid.io"
/// $env:CPE_NILE_APIKEY  = "&lt;optional TronGrid Nile key&gt;"
/// $env:CPE_NILE_PRIVKEY = "&lt;throwaway 64-hex private key&gt;"
/// $env:CPE_NILE_FROM    = "&lt;throwaway T-address holding the funds&gt;"
/// $env:CPE_NILE_CONTRACT= "&lt;Nile TRC-20 token contract, e.g. test USDT&gt;"
/// $env:CPE_NILE_TO      = "&lt;destination T-address&gt;"
/// $env:CPE_NILE_AMOUNT  = "1000000"   # base units (1 USDT at 6dp)
/// dotnet test --filter FullyQualifiedName~WithdrawalNileLiveTests
/// </code>
/// </summary>
[Trait("Category", "LiveTestnet")]
public sealed class WithdrawalNileLiveTests
{
    private const string KeyReference = "kms://tron/hot/nile";

    [Fact]
    public async Task A_real_trc20_transfer_is_built_signed_broadcast_and_confirmed_on_nile()
    {
        var rpcUrl = Env("CPE_NILE_RPC");
        var privateKeyHex = Env("CPE_NILE_PRIVKEY");
        var from = Env("CPE_NILE_FROM");
        var contract = Env("CPE_NILE_CONTRACT");
        var to = Env("CPE_NILE_TO");
        var amountRaw = Env("CPE_NILE_AMOUNT");

        if (string.IsNullOrWhiteSpace(rpcUrl) || string.IsNullOrWhiteSpace(privateKeyHex) || string.IsNullOrWhiteSpace(from)
            || string.IsNullOrWhiteSpace(contract) || string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(amountRaw))
        {
            Assert.Skip("Set CPE_NILE_RPC/APIKEY/PRIVKEY/FROM/CONTRACT/TO/AMOUNT to run the live Nile test (see docs/withdrawal-testnet.md).");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var amount = BigInteger.Parse(amountRaw);
        var assetId = Guid.CreateVersion7();

        using var http = new HttpClient { BaseAddress = new Uri(rpcUrl.EndsWith('/') ? rpcUrl : rpcUrl + "/") };
        if (Env("CPE_NILE_APIKEY") is { Length: > 0 } apiKey)
            http.DefaultRequestHeaders.Add("TRON-PRO-API-KEY", apiKey);

        var rpc = new TronRpc(http);
        var builder = new TronTransactionBuilder(rpc, new SingleAssetCatalog(assetId, contract), new TronOptions(), NullLogger<TronTransactionBuilder>.Instance);
        var signer = new TronSigner(
            InMemorySecretProvider.FromStrings(new Dictionary<string, string> { [KeyReference] = privateKeyHex }),
            NullLogger<TronSigner>.Instance);
        var broadcaster = new TronTransactionBroadcaster(rpc, NullLogger<TronTransactionBroadcaster>.Instance);

        // Build → sign → broadcast, exactly as the withdrawal processing service does.
        var unsigned = await builder.BuildTransferAsync(new BuildWithdrawalRequest(Chain.Tron, assetId, from, to, amount), ct);

        var signed = await signer.SignAsync(new SigningRequest(Guid.CreateVersion7(), Chain.Tron, unsigned.Payload, KeyReference), ct);
        signed.IsSuccess.ShouldBeTrue(signed.Error?.Message);

        var broadcast = await broadcaster.BroadcastAsync(Chain.Tron, signed.Value.SignedPayload, ct);
        broadcast.IsSuccess.ShouldBeTrue(broadcast.Error?.Message); // a real Nile node accepted our signature
        var txId = broadcast.Value.TransactionHash;

        // Poll until the node reports it mined (Nile block time ≈ 3s).
        TransactionStatus? status = null;
        for (var attempt = 0; attempt < 40 && status is null; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            status = await broadcaster.GetTransactionStatusAsync(Chain.Tron, txId, ct);
        }

        status.ShouldNotBeNull($"Transaction {txId} was not mined on Nile within the timeout. Explorer: https://nile.tronscan.org/#/transaction/{txId}");
        status!.Succeeded.ShouldBeTrue($"Transaction {txId} reverted on-chain. Explorer: https://nile.tronscan.org/#/transaction/{txId}");
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    private sealed class SingleAssetCatalog(Guid assetId, string contractAddress) : IAssetCatalog
    {
        private readonly AssetDto _asset = new(assetId, Chain.Tron, "USDT", contractAddress, 6, IsNative: false);
        public Task<AssetDto?> FindByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<AssetDto?>(id == assetId ? _asset : null);
        public Task<AssetDto?> FindAsync(Chain chain, string symbol, CancellationToken ct = default) => Task.FromResult<AssetDto?>(null);
        public Task<IReadOnlyList<AssetDto>> GetActiveAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AssetDto>>([_asset]);
    }
}
