using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Tests;

/// <summary>
/// The dev/testnet staking-wallet seeder: it makes the platform staking (energy) wallet the delegation source
/// the sweep energy gate draws from, by registering its imported signing key (purpose Energy), its
/// <c>WalletType.Energy</c> row, and an auto-stake policy — all idempotently.
/// </summary>
public sealed class EnergyStakingWalletSeederTests
{
    private const string Address = "TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH";
    private const string SecretRef = "tron-staking-0";
    private static readonly Guid DerivedKeyId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IPlatformKeyRegistrar _keys = Substitute.For<IPlatformKeyRegistrar>();
    private readonly IPlatformWalletRegistrar _wallets = Substitute.For<IPlatformWalletRegistrar>();
    private readonly IEnergyPolicyRepository _policies = Substitute.For<IEnergyPolicyRepository>();

    public EnergyStakingWalletSeederTests()
    {
        _keys.RegisterImportedKeyAsync(Chain.Tron, DerivationPurpose.Energy, Address, SecretRef, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RegisteredPlatformKey(DerivedKeyId, Chain.Tron, Address, SecretRef)));
        _wallets.RegisterPlatformWalletAsync(DerivedKeyId, Chain.Tron, Address, "Energy", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RegisteredPlatformWallet(Guid.CreateVersion7(), Chain.Tron, Address, "Energy")));
        _policies.FindAsync(Chain.Tron, "Energy", Arg.Any<CancellationToken>()).Returns((EnergyPolicy?)null);
    }

    private EnergyStakingWalletSeeder Seeder(params StakingWalletSeed[] seeds)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _keys);
        services.AddScoped(_ => _wallets);
        services.AddScoped(_ => _policies);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new EnergyStakingWalletSeeder(
            scopeFactory,
            Options.Create(new EnergyStakingDevOptions { DevStakingWallets = [.. seeds] }),
            NullLogger<EnergyStakingWalletSeeder>.Instance);
    }

    private static StakingWalletSeed TronSeed => new() { Chain = "Tron", Address = Address, SecretReference = SecretRef };

    [Fact]
    public async Task Registers_the_energy_key_the_wallet_row_and_an_auto_stake_policy()
    {
        await Seeder(TronSeed).StartAsync(Ct);

        await _keys.Received(1).RegisterImportedKeyAsync(
            Chain.Tron, DerivationPurpose.Energy, Address, SecretRef, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _wallets.Received(1).RegisterPlatformWalletAsync(
            DerivedKeyId, Chain.Tron, Address, "Energy", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        _policies.Received(1).Add(Arg.Is<EnergyPolicy>(p => p.WalletType == "Energy" && p.EnableAutoStake));
        await _policies.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_existing_policy_is_not_re_added()
    {
        _policies.FindAsync(Chain.Tron, "Energy", Arg.Any<CancellationToken>())
            .Returns(EnergyPolicy.Create(Chain.Tron, "Energy", 1, 2, 1, 0, enableAutoStake: true, enableAutoRent: false).Value);

        await Seeder(TronSeed).StartAsync(Ct);

        _policies.DidNotReceive().Add(Arg.Any<EnergyPolicy>());
    }

    [Fact]
    public async Task If_the_key_cannot_be_registered_the_wallet_row_is_not_created()
    {
        _keys.RegisterImportedKeyAsync(Chain.Tron, DerivationPurpose.Energy, Address, SecretRef, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<RegisteredPlatformKey>(Error.Failure("key.bad", "no secret")));

        await Seeder(TronSeed).StartAsync(Ct);

        await _wallets.DidNotReceive().RegisterPlatformWalletAsync(
            Arg.Any<Guid>(), Arg.Any<Chain>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_chain_is_skipped_without_touching_custody()
    {
        await Seeder(new StakingWalletSeed { Chain = "Dogecoin", Address = Address, SecretReference = SecretRef }).StartAsync(Ct);

        await _keys.DidNotReceive().RegisterImportedKeyAsync(
            Arg.Any<Chain>(), Arg.Any<DerivationPurpose>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
