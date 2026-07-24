using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Handlers;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Signing;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Events;
using CryptoPaymentEngine.Infrastructure.Locking;
using CryptoPaymentEngine.Infrastructure.Outbox;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Shouldly;
using Xunit;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Tests;

/// <summary>
/// Level 3, offline proof: the whole money-out path on a real SQL Server driving the <b>real</b> TRON
/// transaction builder, the <b>real</b> secp256k1 signer (a genuine signature over a throwaway key), and the
/// <b>real</b> broadcaster — with only the Nile node itself replaced by an in-process stub returning canonical
/// TRON responses. It proves the full database state at every step: the withdrawal is reserved, built, signed
/// (blob persisted), broadcast, confirmed, and settled, and the ledger balances move exactly as they must.
/// The live-Nile variant (a real on-chain broadcast) lives behind an environment gate in
/// <see cref="WithdrawalNileLiveTests"/>; this one needs no node, no funds, and runs in CI.
/// </summary>
public sealed class WithdrawalTronTestnetFlowTests : IAsyncLifetime
{
    private const string DbName = "CpeWithdrawalTronTestnetFlowTests";
    private const string KeyReference = "kms://tron/hot/0";
    private const string Destination = "TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH";   // a valid Base58Check TRON address
    private const string UsdtContract = "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t";  // published USDT-TRC20 contract

    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly BigInteger Fee = BigInteger.Parse("100000");
    private static readonly WithdrawalPolicy Policy =
        new(Minimum: BigInteger.Zero, Maximum: null, Fee: Fee, ApprovalThreshold: BigInteger.Parse("5000000"), Confirmations: 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private ServiceProvider _provider = null!;
    private string _hotWalletAddress = null!;

    public async ValueTask InitializeAsync()
    {
        // A throwaway secp256k1 key: its TRON address is the hot wallet, its hex is the signer's secret. This is
        // exactly the shape of the live testnet run — only here the key holds no funds and the node is a stub.
        var key = new Key();
        _hotWalletAddress = new TronAddressEncoder().Encode(key.PubKey.Decompress().ToBytes());
        var privateKeyHex = Convert.ToHexString(key.ToBytes());

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDistributedLockFactory, NoOpLock>();

        services.AddDbContext<WithdrawalDbContext>(o => o.UseSqlServer(ConnectionString).UseBigIntegerMoney());
        services.AddDbContext<LedgerDbContext>(o => o.UseSqlServer(ConnectionString).UseBigIntegerMoney());

        // Ledger (real).
        services.AddScoped<ILedgerAccountStore, LedgerAccountStore>();
        services.AddScoped<ILedgerPostingStore, LedgerPostingStore>();
        services.AddScoped<LedgerPoster>();
        services.AddScoped<ILedgerPoster>(sp => sp.GetRequiredService<LedgerPoster>());
        services.AddScoped<IWithdrawalLedger>(sp => sp.GetRequiredService<LedgerPoster>());
        services.AddScoped<IIntegrationEventHandler<WithdrawalConfirmed>, WithdrawalConfirmedHandler>();
        services.AddScoped<IIntegrationEventHandler<WithdrawalFailed>, WithdrawalFailedHandler>();
        services.AddScoped<IEventBus, InProcessEventBus>();

        // Withdrawal (real).
        services.AddScoped<IWithdrawalRepository, WithdrawalRepository>();
        services.AddScoped<IWithdrawalRequestService, WithdrawalRequestService>();
        services.AddScoped<WithdrawalProcessingService>();
        services.AddScoped<WithdrawalConfirmationService>();
        services.AddSingleton<IWithdrawalPolicyProvider>(new StubPolicy());
        services.AddSingleton<IHotWalletProvider>(new StubHotWallet(_hotWalletAddress));
        services.AddSingleton<IMerchantDirectory>(new FakeMerchants());
        services.AddSingleton<IMerchantFeeSchedule>(new FakeFees(Fee));
        services.AddSingleton<IChainStatusReader>(new StubChainStatus());

        // The Level-3 stack under test: real builder + real broadcaster over a stub Nile node, and the REAL
        // secp256k1 signer over the throwaway key.
        services.AddSingleton<ITronTxRpc>(new StubNileNode());
        services.AddSingleton<IAssetCatalog>(new StubAssetCatalog());
        services.AddSingleton(new TronOptions());
        services.AddSingleton<ITransactionBuilder, TronTransactionBuilder>();
        services.AddSingleton<ITransactionBroadcaster, TronTransactionBroadcaster>();
        services.AddSingleton<ISigner>(new TronSigner(
            InMemorySecretProvider.FromStrings(new Dictionary<string, string> { [KeyReference] = privateKeyHex }),
            NullLogger<TronSigner>.Instance));

        _provider = services.BuildServiceProvider();

        await using var scope = _provider.CreateAsyncScope();
        var w = scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>();
        await w.Database.EnsureDeletedAsync(Ct);
        await w.Database.EnsureCreatedAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database
            .GetService<IRelationalDatabaseCreator>().CreateTablesAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await using (var scope = _provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>().Database.EnsureDeletedAsync(Ct);
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task A_usdt_withdrawal_is_built_signed_broadcast_confirmed_and_settled_end_to_end()
    {
        await SeedMerchantBalanceAsync(BigInteger.Parse("10000000")); // 10 USDT in custody + liability
        var amount = BigInteger.Parse("3000000");                     // below approval threshold → auto-approved

        var request = await RequestAsync(amount, "idem-nile-happy");
        request.IsSuccess.ShouldBeTrue();
        request.Value.Status.ShouldBe(nameof(WithdrawalStatus.Approved));

        // Reserve: merchant debited amount+fee, held in clearing.
        (await BalanceAsync(AccountType.MerchantLiability, Merchant)).ShouldBe(BigInteger.Parse("6900000"));
        (await BalanceAsync(AccountType.WithdrawalClearing, null)).ShouldBe(BigInteger.Parse("3100000"));

        await ProcessAsync(); // real build → real sign → persist signed blob → real broadcast (stub node)

        // The signed transaction is persisted and broadcast; its tx id is recorded.
        var afterBroadcast = await SingleWithdrawalAsync();
        afterBroadcast.Status.ShouldBe(WithdrawalStatus.Broadcast);
        afterBroadcast.TransactionHash.ShouldNotBeNullOrWhiteSpace();
        afterBroadcast.SignedTransaction.ShouldNotBeNull();
        SignedBlobCarriesARecoverableSignature(afterBroadcast.SignedTransaction!);

        await ConfirmAsync();  // stub node reports SUCCESS + enough depth → confirmed
        await DispatchAsync(); // WithdrawalConfirmed → Ledger settle

        var settled = await SingleWithdrawalAsync();
        settled.Status.ShouldBe(WithdrawalStatus.Confirmed);
        settled.TransactionHash.ShouldBe(afterBroadcast.TransactionHash); // same tx id, never re-minted

        // Final ledger state: custody dropped by the amount that left the chain; fee kept as revenue; nothing stuck.
        (await BalanceAsync(AccountType.WithdrawalClearing, null)).ShouldBe(BigInteger.Zero);
        (await BalanceAsync(AccountType.TreasuryAsset, null)).ShouldBe(BigInteger.Parse("7000000"));
        (await BalanceAsync(AccountType.FeeRevenue, null)).ShouldBe(Fee);
        (await BalanceAsync(AccountType.MerchantLiability, Merchant)).ShouldBe(BigInteger.Parse("6900000"));
    }

    [Fact]
    public async Task Re_processing_a_signed_withdrawal_re_broadcasts_the_same_blob_and_never_double_sends()
    {
        await SeedMerchantBalanceAsync(BigInteger.Parse("10000000"));

        await RequestAsync(BigInteger.Parse("3000000"), "idem-nile-retry");
        await ProcessAsync(); // build + sign + broadcast once

        var afterFirst = await SingleWithdrawalAsync();
        var signedBlob = afterFirst.SignedTransaction!;
        var txId = afterFirst.TransactionHash!;

        // Force the withdrawal back to Signing (simulating a crash after signing, before/around broadcast) and
        // re-run: the service must re-broadcast the SAME persisted blob, keeping the SAME tx id — never rebuild.
        await ForceBackToSigningAsync();
        await ProcessAsync();

        var afterRetry = await SingleWithdrawalAsync();
        afterRetry.Status.ShouldBe(WithdrawalStatus.Broadcast);
        afterRetry.TransactionHash.ShouldBe(txId);                       // identical id → chain dedups, no double-send
        afterRetry.SignedTransaction.ShouldBe(signedBlob);              // same signed bytes re-broadcast
    }

    private static void SignedBlobCarriesARecoverableSignature(byte[] signedBlob)
    {
        var obj = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(signedBlob))!.AsObject();
        var sigHex = obj["signature"]!.AsArray()[0]!.GetValue<string>();
        var sig = Convert.FromHexString(sigHex);
        sig.Length.ShouldBe(65); // r(32) ‖ s(32) ‖ recId(1)

        // The signature recovers a public key over sha256(raw_data) — i.e. a real, node-verifiable signature.
        var rawData = Convert.FromHexString(obj["raw_data_hex"]!.GetValue<string>());
        var compact = new CompactSignature(sig[64], sig[..64]);
        PubKey.RecoverCompact(new uint256(SHA256.HashData(rawData)), compact).ShouldNotBeNull();
    }

    // ── helpers ──

    private async Task<WithdrawalEntity> SingleWithdrawalAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>().Withdrawals.SingleAsync(Ct);
    }

    private async Task ForceBackToSigningAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>();
        // Reset only the workflow status (the signed blob + tx id stay persisted), reproducing a resumed pass.
        await db.Database.ExecuteSqlRawAsync(
            $"UPDATE [withdrawal].[Withdrawal] SET [Status] = '{nameof(WithdrawalStatus.Signing)}'", Ct);
    }

    private async Task SeedMerchantBalanceAsync(BigInteger amount)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILedgerPoster>()
            .CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, amount), Ct);
    }

    private async Task<Result<WithdrawalResult>> RequestAsync(BigInteger amount, string idempotencyKey)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IWithdrawalRequestService>()
            .RequestAsync(new RequestWithdrawalCommand(Merchant, Asset, Chain.Tron, Destination, amount, idempotencyKey), Ct);
    }

    private async Task ProcessAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<WithdrawalProcessingService>().ProcessOnceAsync(Ct);
    }

    private async Task ConfirmAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<WithdrawalConfirmationService>().TrackOnceAsync(Ct);
    }

    private async Task DispatchAsync()
    {
        var dispatcher = new OutboxDispatcher<WithdrawalDbContext>(
            _provider.GetRequiredService<IServiceScopeFactory>(), new NoOpLock(), TimeProvider.System,
            NullLogger<OutboxDispatcher<WithdrawalDbContext>>.Instance);
        await dispatcher.DispatchPendingAsync(Ct);
    }

    private async Task<BigInteger> BalanceAsync(AccountType type, Guid? ownerId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        return await ledger.AccountBalances
            .Join(ledger.Accounts, b => b.Id, a => a.Id, (b, a) => new { b, a })
            .Where(x => x.a.AccountType == type && x.a.OwnerId == ownerId && x.a.AssetId == Asset)
            .Select(x => x.b.Balance)
            .SingleOrDefaultAsync(Ct);
    }

    // ── stubs ──

    /// <summary>A canonical Nile node: builds a real (txID = sha256(raw_data)) unsigned tx, accepts the broadcast,
    /// and reports the tx mined and SUCCESS one block below the tip — enough for the policy's confirmation depth.</summary>
    private sealed class StubNileNode : ITronTxRpc
    {
        public Task<TronTriggerResultDto> TriggerSmartContractAsync(TriggerSmartContractRequest request, CancellationToken ct = default)
        {
            var rawData = RandomNumberGenerator.GetBytes(48);
            var txId = Convert.ToHexString(SHA256.HashData(rawData)).ToLowerInvariant();
            var tx = new JsonObject
            {
                ["txID"] = txId,
                ["raw_data"] = new JsonObject { ["contract"] = new JsonArray() },
                ["raw_data_hex"] = Convert.ToHexString(rawData).ToLowerInvariant(),
                ["visible"] = false,
            };
            return Task.FromResult(new TronTriggerResultDto
            {
                Result = new TronTriggerReturnDto { Result = true },
                Transaction = JsonSerializer.Deserialize<JsonElement>(tx.ToJsonString()),
            });
        }

        public Task<TronBroadcastResultDto> BroadcastTransactionAsync(JsonElement signedTransaction, CancellationToken ct = default) =>
            Task.FromResult(new TronBroadcastResultDto { Result = true });

        public Task<TronTransactionInfoDto?> GetTransactionInfoAsync(string transactionId, CancellationToken ct = default) =>
            Task.FromResult<TronTransactionInfoDto?>(new TronTransactionInfoDto
            {
                Id = transactionId,
                BlockNumber = 999, // tip is 1000 (StubChainStatus) → 2 confirmations ≥ policy's 1
                Receipt = new TronReceiptDto { Result = "SUCCESS" },
            });
    }

    private sealed class StubAssetCatalog : IAssetCatalog
    {
        private static readonly AssetDto Usdt = new(Asset, Chain.Tron, "USDT", UsdtContract, 6, IsNative: false);
        public Task<AssetDto?> FindByIdAsync(Guid assetId, CancellationToken ct = default) =>
            Task.FromResult<AssetDto?>(assetId == Asset ? Usdt : null);
        public Task<AssetDto?> FindAsync(Chain chain, string symbol, CancellationToken ct = default) => Task.FromResult<AssetDto?>(null);
        public Task<IReadOnlyList<AssetDto>> GetActiveAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AssetDto>>([Usdt]);
    }

    private sealed class StubPolicy : IWithdrawalPolicyProvider
    {
        public WithdrawalPolicy For(Chain chain) => Policy;
    }

    private sealed class StubHotWallet(string address) : IHotWalletProvider
    {
        public HotWallet For(Chain chain) => new(address, KeyReference);
    }

    private sealed class FakeMerchants : IMerchantDirectory
    {
        public Task<MerchantSummary?> FindByIdAsync(Guid merchantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MerchantSummary?>(new MerchantSummary(merchantId, "ACME", "Acme", null, CanTransact: true));
        public Task<MerchantSummary?> FindByCodeAsync(string merchantCode, CancellationToken cancellationToken = default) =>
            Task.FromResult<MerchantSummary?>(null);
    }

    private sealed class FakeFees(BigInteger withdrawalFee) : IMerchantFeeSchedule
    {
        public Task<BigInteger> QuoteDepositFeeAsync(Guid merchantId, Guid assetId, BigInteger receivedAmount, CancellationToken cancellationToken = default) =>
            Task.FromResult(BigInteger.Zero);
        public Task<BigInteger> QuoteWithdrawalFeeAsync(Guid merchantId, Guid assetId, BigInteger amount, CancellationToken cancellationToken = default) =>
            Task.FromResult(withdrawalFee);
        public Task<Result<BigInteger>> GrossUpDepositAsync(Guid merchantId, Guid assetId, BigInteger netTarget, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(netTarget));
    }

    private sealed class StubChainStatus : IChainStatusReader
    {
        public Task<long> GetTipHeightAsync(Chain chain, CancellationToken cancellationToken = default) => Task.FromResult(1000L);
        public Task<BlockRef?> GetBlockAsync(Chain chain, long blockNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult<BlockRef?>(new BlockRef(blockNumber, "0xblock"));
        public Task<long> GetFinalizedHeightAsync(Chain chain, CancellationToken cancellationToken = default) => Task.FromResult(1000L);
    }

    private sealed class NoOpLock : IDistributedLockFactory
    {
        public Task<IAsyncDisposable> AcquireAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable>(new Handle());

        private sealed class Handle : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
