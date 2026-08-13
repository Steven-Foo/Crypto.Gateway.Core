using System.Numerics;
using System.Text;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Tests;

/// <summary>
/// Drives energy operations through build → sign → broadcast. The money-sensitive bit tested here: a
/// <see cref="EnergyOperationKind.TopUp"/> is built as a <b>native-TRX transfer FROM the gas hub TO the target</b>
/// (not the other way round), via <see cref="INativeTransferBuilder"/> rather than the resource builder.
/// </summary>
public sealed class EnergyOperationProcessingServiceTests
{
    private const string Hub = "THub";
    private const string Deposit = "TDeposit";
    private static readonly Guid StakingWalletId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IResourceOperationBuilder _resourceBuilder = Substitute.For<IResourceOperationBuilder>();
    private readonly INativeTransferBuilder _nativeBuilder = Substitute.For<INativeTransferBuilder>();
    private readonly ISigner _signer = Substitute.For<ISigner>();
    private readonly ITransactionBroadcaster _broadcaster = Substitute.For<ITransactionBroadcaster>();
    private readonly IPlatformSigningKeyDirectory _signingKeys = Substitute.For<IPlatformSigningKeyDirectory>();
    private readonly InMemoryOpRepo _repo = new();

    public EnergyOperationProcessingServiceTests()
    {
        _signingKeys.FindActiveAsync(Chain.Tron, DerivationPurpose.Energy, Arg.Any<CancellationToken>())
            .Returns(new PlatformSigningKey(StakingWalletId, Chain.Tron, "hub-key-ref"));
        _signer.SignAsync(Arg.Any<SigningRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SignedTransaction(Encoding.UTF8.GetBytes("signed"))));
        _broadcaster.BroadcastAsync(Chain.Tron, Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new BroadcastResult("0xtx")));
    }

    private EnergyOperationProcessingService Service => new(
        _repo, _resourceBuilder, _nativeBuilder, _signer, _broadcaster, _signingKeys,
        TimeProvider.System, NullLogger<EnergyOperationProcessingService>.Instance);

    [Fact]
    public async Task A_topup_is_built_as_a_native_transfer_from_the_hub_to_the_target_and_broadcast()
    {
        _nativeBuilder.BuildNativeTransferAsync(Chain.Tron, Hub, Deposit, Arg.Any<BigInteger>(), Arg.Any<CancellationToken>())
            .Returns(new UnsignedTransaction(Encoding.UTF8.GetBytes("unsigned-topup")));

        _repo.Items.Add(EnergyOperation.CreateTopUp(StakingWalletId, Chain.Tron, Hub, Deposit, new BigInteger(2_000_000), DateTimeOffset.UtcNow).Value);

        await Service.ProcessOnceAsync(Ct);

        // Built via the NATIVE transfer path (hub → deposit), never the resource (freeze/delegate) builder.
        await _nativeBuilder.Received(1).BuildNativeTransferAsync(
            Chain.Tron, Hub, Deposit, new BigInteger(2_000_000), Arg.Any<CancellationToken>());
        await _resourceBuilder.DidNotReceive().BuildDelegateEnergyAsync(Arg.Any<DelegateEnergyRequest>(), Arg.Any<CancellationToken>());

        _repo.Items.Single().Status.ShouldBe(EnergyOperationStatus.Broadcast);
    }

    private sealed class InMemoryOpRepo : IEnergyOperationRepository
    {
        public readonly List<EnergyOperation> Items = [];

        public Task<IReadOnlyList<EnergyOperation>> GetByStatusesAsync(
            IReadOnlyCollection<EnergyOperationStatus> statuses, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EnergyOperation>>(Items.Where(o => statuses.Contains(o.Status)).ToList());

        public Task<bool> HasInFlightStakeAsync(Guid stakingWalletId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> HasInFlightDelegateAsync(Chain chain, string targetAddress, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> HasInFlightTopUpAsync(Chain chain, string targetAddress, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> TryAddAsync(EnergyOperation operation, CancellationToken cancellationToken = default) { Items.Add(operation); return Task.FromResult(true); }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
