using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Tests;

/// <summary>
/// The processing service builds → signs → broadcasts a Pending sweep, never touching a key (it quotes a key
/// reference to the signer, §10). It fails safely when no signing key resolves, and re-broadcasts the SAME
/// persisted blob for a resumed sweep rather than rebuilding (the double-send guard).
/// </summary>
public sealed class SweepProcessingServiceTests
{
    private static readonly byte[] Unsigned = [9, 9];
    private static readonly byte[] SignedBytes = [7, 7];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ISweepRepository _repository = Substitute.For<ISweepRepository>();
    private readonly ITransactionBuilder _builder = Substitute.For<ITransactionBuilder>();
    private readonly ISigner _signer = Substitute.For<ISigner>();
    private readonly ITransactionBroadcaster _broadcaster = Substitute.For<ITransactionBroadcaster>();
    private readonly IDepositSigningKeyDirectory _signingKeys = Substitute.For<IDepositSigningKeyDirectory>();
    private readonly IEnergyDelegationService _energy = Substitute.For<IEnergyDelegationService>();

    public SweepProcessingServiceTests() =>
        // Default: the source address already has energy, so processing proceeds (dev's in-memory behaviour).
        _energy.EnsureEnergyForTransferAsync(Chain.Tron, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(EnergyReadiness.Ready);

    private SweepProcessingService Service => new(
        _repository, _builder, _signer, _broadcaster, _signingKeys, _energy, TimeProvider.System,
        NullLogger<SweepProcessingService>.Instance);

    private static Domain.Sweep NewSweep() =>
        Domain.Sweep.Create(Guid.CreateVersion7(), Chain.Tron, Guid.CreateVersion7(), "TFrom", "THot", 5_000_000, DateTimeOffset.UtcNow).Value;

    private void GivenPipelineSucceeds()
    {
        _signingKeys.FindByAddressAsync(Chain.Tron, "TFrom", Arg.Any<CancellationToken>())
            .Returns(new DepositSigningKey(Guid.CreateVersion7(), Chain.Tron, "seed-ref#0"));
        _builder.BuildTransferAsync(Arg.Any<BuildWithdrawalRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UnsignedTransaction(Unsigned));
        _signer.SignAsync(Arg.Any<SigningRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SignedTransaction(SignedBytes)));
        _broadcaster.BroadcastAsync(Chain.Tron, Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new BroadcastResult("0xhash")));
    }

    [Fact]
    public async Task A_pending_sweep_is_built_signed_and_broadcast()
    {
        var sweep = NewSweep();
        _repository.GetByStatusesAsync(Arg.Any<IReadOnlyCollection<SweepStatus>>(), Arg.Any<CancellationToken>())
            .Returns([sweep]);
        GivenPipelineSucceeds();

        var processed = await Service.ProcessOnceAsync(Ct);

        processed.ShouldBe(1);
        sweep.Status.ShouldBe(SweepStatus.Broadcast);
        sweep.TransactionHash.ShouldBe("0xhash");
        // The key reference is quoted to the signer; the service never sees key material (§10).
        await _signer.Received(1).SignAsync(
            Arg.Is<SigningRequest>(r => r.KeyReference == "seed-ref#0"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_sweep_waits_without_building_when_the_source_address_has_no_energy_yet()
    {
        var sweep = NewSweep();
        _repository.GetByStatusesAsync(Arg.Any<IReadOnlyCollection<SweepStatus>>(), Arg.Any<CancellationToken>())
            .Returns([sweep]);
        _energy.EnsureEnergyForTransferAsync(Chain.Tron, "TFrom", Arg.Any<CancellationToken>())
            .Returns(EnergyReadiness.Provisioning); // delegation in flight, not ready

        await Service.ProcessOnceAsync(Ct);

        // The sweep stays Pending (never failed, never broadcast) — funds are safe, retried next pass.
        sweep.Status.ShouldBe(SweepStatus.Pending);
        await _builder.DidNotReceive().BuildTransferAsync(Arg.Any<BuildWithdrawalRequest>(), Arg.Any<CancellationToken>());
        await _signer.DidNotReceive().SignAsync(Arg.Any<SigningRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_sweep_with_no_signing_key_fails_before_building_anything()
    {
        var sweep = NewSweep();
        _repository.GetByStatusesAsync(Arg.Any<IReadOnlyCollection<SweepStatus>>(), Arg.Any<CancellationToken>())
            .Returns([sweep]);
        _signingKeys.FindByAddressAsync(Chain.Tron, "TFrom", Arg.Any<CancellationToken>())
            .Returns((DepositSigningKey?)null);

        await Service.ProcessOnceAsync(Ct);

        sweep.Status.ShouldBe(SweepStatus.Failed);
        await _builder.DidNotReceive().BuildTransferAsync(Arg.Any<BuildWithdrawalRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_resumed_sweep_rebroadcasts_the_same_blob_without_rebuilding_or_resigning()
    {
        var sweep = NewSweep();
        sweep.RecordSigned(Guid.CreateVersion7(), SignedBytes, DateTimeOffset.UtcNow); // already Signing, blob persisted
        _repository.GetByStatusesAsync(Arg.Any<IReadOnlyCollection<SweepStatus>>(), Arg.Any<CancellationToken>())
            .Returns([sweep]);
        _broadcaster.BroadcastAsync(Chain.Tron, Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new BroadcastResult("0xhash")));

        await Service.ProcessOnceAsync(Ct);

        sweep.Status.ShouldBe(SweepStatus.Broadcast);
        // Re-broadcast the EXACT persisted blob — never rebuild or re-sign (double-send guard).
        await _broadcaster.Received(1).BroadcastAsync(Chain.Tron, SignedBytes, Arg.Any<CancellationToken>());
        await _builder.DidNotReceive().BuildTransferAsync(Arg.Any<BuildWithdrawalRequest>(), Arg.Any<CancellationToken>());
        await _signer.DidNotReceive().SignAsync(Arg.Any<SigningRequest>(), Arg.Any<CancellationToken>());
    }
}
