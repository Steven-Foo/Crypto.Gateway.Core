using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Tests;

public sealed class TreasuryReloadServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly Guid TargetWallet = Guid.CreateVersion7();

    private readonly ITreasuryColdWalletDirectory _cold = Substitute.For<ITreasuryColdWalletDirectory>();
    private readonly ITreasuryHotWalletDirectory _hot = Substitute.For<ITreasuryHotWalletDirectory>();
    private readonly ITransactionBuilder _builder = Substitute.For<ITransactionBuilder>();
    private readonly ITransactionBroadcaster _broadcaster = Substitute.For<ITransactionBroadcaster>();
    private readonly IChainStatusReader _chainStatus = Substitute.For<IChainStatusReader>();
    private readonly InMemoryReloadRepo _repo = new();

    public TreasuryReloadServiceTests()
    {
        _cold.GetAsync(Chain.Tron, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ColdTreasuryWallet(Chain.Tron, "TCold")));
        _hot.GetHotWalletPoolAsync(Chain.Tron, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TreasuryHotWallet>>([new TreasuryHotWallet(TargetWallet, Chain.Tron, "THot", "ref")]));
        _builder.BuildTransferAsync(Arg.Any<BuildWithdrawalRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UnsignedTransaction([1, 2, 3]));
    }

    private TreasuryReloadService Service() => new(_cold, _hot, _builder, _repo, TimeProvider.System);

    private TreasuryReloadProcessingService Processing(int confirmations = 1) => new(
        _repo, _broadcaster, _chainStatus, new TreasuryReloadOptions { Confirmations = confirmations },
        TimeProvider.System, NullLogger<TreasuryReloadProcessingService>.Instance);

    [Fact]
    public async Task Initiate_builds_the_unsigned_cold_to_target_transfer_and_stores_awaiting_signature()
    {
        var result = await Service().InitiateAsync(Chain.Tron, Asset, TargetWallet, BigInteger.Parse("5000000"), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.UnsignedTransactionHex.ShouldBe(Convert.ToHexString(new byte[] { 1, 2, 3 }));

        var stored = _repo.Items.ShouldHaveSingleItem();
        stored.Status.ShouldBe(TreasuryReloadStatus.AwaitingSignature);
        stored.SourceAddress.ShouldBe("TCold");
        stored.TargetAddress.ShouldBe("THot");
        stored.TargetWalletId.ShouldBe(TargetWallet);

        await _builder.Received(1).BuildTransferAsync(
            Arg.Is<BuildWithdrawalRequest>(r => r.FromAddress == "TCold" && r.ToAddress == "THot" && r.Amount == 5_000_000),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Initiate_fails_when_no_cold_treasury_is_registered()
    {
        _cold.GetAsync(Chain.Tron, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ColdTreasuryWallet>(TreasuryReloadErrors.ColdWalletNotConfigured));

        var result = await Service().InitiateAsync(Chain.Tron, Asset, TargetWallet, BigInteger.Parse("5000000"), Ct);

        result.Error!.Code.ShouldBe(TreasuryReloadErrors.ColdWalletNotConfigured.Code);
        _repo.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Initiate_fails_when_the_target_wallet_is_not_in_the_pool()
    {
        var result = await Service().InitiateAsync(Chain.Tron, Asset, Guid.NewGuid(), BigInteger.Parse("5000000"), Ct);
        result.Error!.Code.ShouldBe(TreasuryReloadErrors.TargetWalletRequired.Code);
    }

    [Fact]
    public async Task Submit_stores_the_signed_blob()
    {
        var initiated = (await Service().InitiateAsync(Chain.Tron, Asset, TargetWallet, BigInteger.Parse("5000000"), Ct)).Value;

        (await Service().SubmitSignedAsync(initiated.ReloadId, [9, 9], Ct)).IsSuccess.ShouldBeTrue();

        _repo.Items.Single().Status.ShouldBe(TreasuryReloadStatus.Signed);
    }

    [Fact]
    public async Task Processing_broadcasts_then_confirms_a_submitted_reload()
    {
        _broadcaster.BroadcastAsync(Chain.Tron, Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new BroadcastResult("0xtx")));
        _broadcaster.GetTransactionStatusAsync(Chain.Tron, "0xtx", Arg.Any<CancellationToken>())
            .Returns(new TransactionStatus(100, Succeeded: true));
        _chainStatus.GetTipHeightAsync(Chain.Tron, Arg.Any<CancellationToken>()).Returns(100L);

        var initiated = (await Service().InitiateAsync(Chain.Tron, Asset, TargetWallet, BigInteger.Parse("5000000"), Ct)).Value;
        await Service().SubmitSignedAsync(initiated.ReloadId, [9, 9], Ct);

        await Processing().ProcessOnceAsync(Ct); // Signed → Broadcast
        _repo.Items.Single().Status.ShouldBe(TreasuryReloadStatus.Broadcast);

        await Processing().ProcessOnceAsync(Ct); // Broadcast → Confirmed
        _repo.Items.Single().Status.ShouldBe(TreasuryReloadStatus.Confirmed);
    }

    private sealed class InMemoryReloadRepo : ITreasuryReloadRepository
    {
        public readonly List<TreasuryReload> Items = [];

        public Task AddAsync(TreasuryReload reload, CancellationToken cancellationToken = default)
        {
            Items.Add(reload);
            return Task.CompletedTask;
        }

        public Task<TreasuryReload?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.FirstOrDefault(x => x.Id == id));

        public Task<IReadOnlyList<TreasuryReload>> GetByStatusesAsync(
            IReadOnlyCollection<TreasuryReloadStatus> statuses, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TreasuryReload>>(Items.Where(x => statuses.Contains(x.Status)).ToList());

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
