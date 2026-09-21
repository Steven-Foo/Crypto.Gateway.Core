using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Tests;

/// <summary>
/// Registering and designating the cold collection wallets — the addresses every swept deposit ends up in.
/// The money-relevant rules: a malformed address never becomes a destination, an address is never edited in
/// place (which would drop the old one from the custody audit while it still holds funds), exactly one
/// wallet per (chain, kind) is active, and a screening verdict is recorded but never refuses a registration.
/// </summary>
public sealed class TreasuryColdWalletRegistrationServiceTests
{
    private const string First = "TFirstColdTreasuryAddress000000001";
    private const string Second = "TSecondColdTreasuryAddress00000002";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ITreasuryColdWalletRepository _repository = Substitute.For<ITreasuryColdWalletRepository>();
    private readonly IAddressEncoderFactory _encoders = Substitute.For<IAddressEncoderFactory>();
    private readonly IAddressScreeningService _screening = Substitute.For<IAddressScreeningService>();
    private readonly TreasuryScreeningOptions _screeningOptions = new() { ScreenCollectionWallets = false };

    public TreasuryColdWalletRegistrationServiceTests()
    {
        // No encoder for the chain ⇒ no format check, which is how an unsupported chain behaves in the host.
        _encoders.Supports(Arg.Any<Chain>()).Returns(false);

        // The designation path runs inside a transaction seam; the fake just runs the body.
        _repository.InTransactionAsync(Arg.Any<Func<CancellationToken, Task<string?>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<string?>>>()(Ct));
    }

    private TreasuryColdWalletRegistrationService NewRegistrar(IAddressScreeningService? screening = null) => new(
        _repository, _encoders, TimeProvider.System, Options.Create(_screeningOptions),
        NullLogger<TreasuryColdWalletRegistrationService>.Instance, screening);

    [Fact]
    public async Task A_new_address_is_registered_retired_unless_activation_is_asked_for()
    {
        var result = await NewRegistrar().RegisterAsync(
            new RegisterColdWalletCommand(Chain.Tron, ColdWalletKind.Safe, $"  {First} "), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Address.ShouldBe(First);
        result.Value.Status.ShouldBe(ColdWalletStatus.Retired);
        result.Value.ReplacedAddress.ShouldBeNull();
        await _repository.Received(1).AddAsync(
            Arg.Is<TreasuryColdWallet>(w => w.Address == First && !w.IsActive), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Activating_a_replacement_retires_the_wallet_it_displaces_and_reports_its_address()
    {
        var current = TreasuryColdWallet.Register(
            Chain.Tron, ColdWalletKind.Safe, First, label: null, activate: true, DateTimeOffset.UtcNow).Value;
        _repository.FindActiveAsync(Chain.Tron, ColdWalletKind.Safe, Arg.Any<CancellationToken>()).Returns(current);

        var result = await NewRegistrar().RegisterAsync(
            new RegisterColdWalletCommand(Chain.Tron, ColdWalletKind.Safe, Second, Label: "cold B", Activate: true), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe(ColdWalletStatus.Active);
        result.Value.ReplacedAddress.ShouldBe(First);
        current.Status.ShouldBe(ColdWalletStatus.Retired);
    }

    /// <summary>Re-registering the same address adopts the existing wallet rather than creating a second —
    /// what makes the boot-time dev seed safe to run on every start.</summary>
    [Fact]
    public async Task Registering_the_same_address_again_adopts_the_existing_wallet()
    {
        var existing = TreasuryColdWallet.Register(
            Chain.Tron, ColdWalletKind.Safe, First, label: null, activate: true, DateTimeOffset.UtcNow).Value;
        _repository.FindByAddressAsync(Chain.Tron, First, Arg.Any<CancellationToken>()).Returns(existing);

        var result = await NewRegistrar().RegisterAsync(
            new RegisterColdWalletCommand(Chain.Tron, ColdWalletKind.Safe, First, Activate: true), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.WalletId.ShouldBe(existing.Id);
        await _repository.DidNotReceive().AddAsync(Arg.Any<TreasuryColdWallet>(), Arg.Any<CancellationToken>());
    }

    /// <summary>One address cannot be both the clean and the quarantine destination; allowing it would make
    /// the segregation meaningless while looking like it was in force.</summary>
    [Fact]
    public async Task The_same_address_cannot_be_registered_under_the_other_kind()
    {
        var existing = TreasuryColdWallet.Register(
            Chain.Tron, ColdWalletKind.Safe, First, label: null, activate: true, DateTimeOffset.UtcNow).Value;
        _repository.FindByAddressAsync(Chain.Tron, First, Arg.Any<CancellationToken>()).Returns(existing);

        var result = await NewRegistrar().RegisterAsync(
            new RegisterColdWalletCommand(Chain.Tron, ColdWalletKind.Danger, First), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("treasury.cold_wallet.already_registered");
    }

    [Fact]
    public async Task An_empty_address_is_refused()
    {
        var result = await NewRegistrar().RegisterAsync(
            new RegisterColdWalletCommand(Chain.Tron, ColdWalletKind.Safe, "  "), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("treasury.cold_wallet.address_required");
    }

    /// <summary>The check a payout destination already gets. A typo here would become the destination of
    /// every future sweep on the chain, so it is caught before the row exists.</summary>
    [Fact]
    public async Task A_malformed_address_for_the_chain_is_refused()
    {
        var encoder = Substitute.For<IAddressEncoder>();
        encoder.IsValidAddress(Arg.Any<string>()).Returns(false);
        _encoders.Supports(Chain.Tron).Returns(true);
        _encoders.For(Chain.Tron).Returns(encoder);

        var result = await NewRegistrar().RegisterAsync(
            new RegisterColdWalletCommand(Chain.Tron, ColdWalletKind.Safe, "not-an-address"), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("treasury.cold_wallet.invalid_address");
        await _repository.DidNotReceive().AddAsync(Arg.Any<TreasuryColdWallet>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_active_destination_cannot_be_retired_leaving_the_chain_with_none()
    {
        var active = TreasuryColdWallet.Register(
            Chain.Tron, ColdWalletKind.Safe, First, label: null, activate: true, DateTimeOffset.UtcNow).Value;
        _repository.FindByIdAsync(active.Id, Arg.Any<CancellationToken>()).Returns(active);

        var result = await NewRegistrar().RetireAsync(active.Id, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("treasury.cold_wallet.cannot_retire_active");
        active.Status.ShouldBe(ColdWalletStatus.Active);
    }

    /// <summary>Screening records and warns; it never refuses. A Block on a Safe address is surfaced loudly
    /// so a human looks at it, but the platform is not left unable to register a destination.</summary>
    [Fact]
    public async Task A_blocked_address_is_still_registered_with_the_verdict_recorded_and_a_warning_returned()
    {
        _screeningOptions.ScreenCollectionWallets = true;
        var screeningId = Guid.CreateVersion7();
        _screening.ScreenAsync(Chain.Tron, First, ScreeningPurpose.ColdCollectionWallet, Arg.Any<CancellationToken>())
            .Returns(new ScreeningVerdict(
                screeningId, ScreeningDecision.Block, 92, "Severe", ["Sanctioned Entity"], null, null,
                DateTimeOffset.UtcNow, FromCache: false));

        var result = await NewRegistrar(_screening).RegisterAsync(
            new RegisterColdWalletCommand(Chain.Tron, ColdWalletKind.Safe, First), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ScreeningDecision.ShouldBe("Block");
        result.Value.ScreeningScore.ShouldBe(92);
        result.Value.ScreeningId.ShouldBe(screeningId);
        result.Value.Warnings.ShouldNotBeEmpty();
    }

    /// <summary>The quarantine wallet collects tainted funds, so a poor score on it is the control working.
    /// Warning about it every time is how a real alarm gets ignored.</summary>
    [Fact]
    public async Task A_flagged_danger_wallet_is_recorded_without_a_warning()
    {
        _screeningOptions.ScreenCollectionWallets = true;
        _screening.ScreenAsync(Chain.Tron, First, ScreeningPurpose.ColdCollectionWallet, Arg.Any<CancellationToken>())
            .Returns(new ScreeningVerdict(
                Guid.CreateVersion7(), ScreeningDecision.Block, 92, "Severe", ["Mixer"], null, null,
                DateTimeOffset.UtcNow, FromCache: false));

        var result = await NewRegistrar(_screening).RegisterAsync(
            new RegisterColdWalletCommand(Chain.Tron, ColdWalletKind.Danger, First), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ScreeningDecision.ShouldBe("Block");
        result.Value.Warnings.ShouldBeEmpty();
    }

    /// <summary>Screening off means no provider call at all, rather than a call that reports "unknown" —
    /// proven by a stub that throws if reached, so passing is evidence rather than the absence of it.</summary>
    [Fact]
    public async Task With_screening_off_the_provider_is_never_called()
    {
        var screening = Substitute.For<IAddressScreeningService>();
        screening.ScreenAsync(Arg.Any<Chain>(), Arg.Any<string>(), Arg.Any<ScreeningPurpose>(), Arg.Any<CancellationToken>())
            .Returns<ScreeningVerdict>(_ => throw new InvalidOperationException("must not screen when switched off"));

        var result = await NewRegistrar(screening).RegisterAsync(
            new RegisterColdWalletCommand(Chain.Tron, ColdWalletKind.Safe, First), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ScreeningDecision.ShouldBeNull();
    }

    /// <summary>Enabled but not composed: recorded as unscreened WITH a warning, never silently skipped. It
    /// does not fail the call — refusing to record a destination because a vendor is absent would stop an
    /// operator fixing a chain that has none.</summary>
    [Fact]
    public async Task Screening_enabled_without_a_provider_warns_rather_than_failing_or_going_quiet()
    {
        _screeningOptions.ScreenCollectionWallets = true;

        var result = await NewRegistrar(screening: null).RegisterAsync(
            new RegisterColdWalletCommand(Chain.Tron, ColdWalletKind.Safe, First), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ScreeningDecision.ShouldBeNull();
        result.Value.Warnings.ShouldNotBeEmpty();
    }
}
