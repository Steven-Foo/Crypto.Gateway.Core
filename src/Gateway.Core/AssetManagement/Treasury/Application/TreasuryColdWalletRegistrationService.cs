using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;

/// <summary>What staff are registering. <paramref name="Activate"/> false adds the address without pointing
/// sweeps at it, which is the safe default for a destination someone has just typed in.</summary>
public sealed record RegisterColdWalletCommand(
    Chain Chain, ColdWalletKind Kind, string Address, string? Label = null, bool Activate = false);

/// <summary>
/// The outcome of a registration or a designation change.
/// </summary>
/// <param name="ReplacedAddress">The address that stopped receiving sweeps because this one took over, or
/// null when nothing was displaced. Audited: redirecting a chain's sweeps is the highest-consequence change
/// on this screen.</param>
/// <param name="Warnings">Operator-facing notes about an address that was accepted but did not come back
/// clean — or was not screened at all. Empty is the normal case and must stay silent.</param>
public sealed record ColdWalletRegistration(
    Guid WalletId,
    Chain Chain,
    ColdWalletKind Kind,
    string Address,
    string? Label,
    ColdWalletStatus Status,
    string? ScreeningDecision,
    int? ScreeningScore,
    Guid? ScreeningId,
    string? ReplacedAddress,
    IReadOnlyList<string> Warnings);

public interface ITreasuryColdWalletRegistrar
{
    /// <summary>
    /// Registers a cold collection address. Screening never refuses one: a Danger wallet is expected to
    /// score badly (it collects flagged funds), and refusing a Safe one on a vendor's say-so would leave a
    /// chain with nowhere to sweep. A non-clean verdict comes back as a warning and is recorded on the row.
    ///
    /// <para>Idempotent for the same (chain, kind, address): re-registering adopts the existing wallet, so a
    /// boot-time seed can run on every start.</para>
    /// </summary>
    Task<Result<ColdWalletRegistration>> RegisterAsync(
        RegisterColdWalletCommand command, CancellationToken cancellationToken = default);

    /// <summary>Makes a registered wallet the destination for its (chain, kind), retiring the one it
    /// replaces in the same transaction.</summary>
    Task<Result<ColdWalletRegistration>> ActivateAsync(Guid walletId, CancellationToken cancellationToken = default);

    /// <summary>Stops a wallet receiving sweeps. Refused for the wallet a chain is currently sweeping into —
    /// activate its replacement instead, which retires it as part of the same change.</summary>
    Task<Result<ColdWalletRegistration>> RetireAsync(Guid walletId, CancellationToken cancellationToken = default);

    /// <summary>Screens the address again, bypassing the cache, and refreshes the verdict stored on the
    /// row. Spends provider quota, so it stays a deliberate staff action.</summary>
    Task<Result<ColdWalletRegistration>> ReScreenAsync(Guid walletId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Registers and designates the platform's cold collection wallets — public, watch-only addresses with no
/// key in the system (§10). Backs both the dev config seed and the staff ops actions.
///
/// <para>Two safety rules live here rather than at the edge, because every caller needs them: the address
/// must be well-formed for its chain (the same <see cref="IAddressEncoderFactory"/> check a payout
/// destination gets — a typo here would become the destination of every future sweep), and changing where a
/// chain sweeps is an add-and-activate, never an edit of an address in place.</para>
/// </summary>
public sealed class TreasuryColdWalletRegistrationService(
    ITreasuryColdWalletRepository repository,
    IAddressEncoderFactory addressEncoders,
    TimeProvider timeProvider,
    IOptions<TreasuryScreeningOptions> screeningOptions,
    ILogger<TreasuryColdWalletRegistrationService> logger,
    IAddressScreeningService? screening = null) : ITreasuryColdWalletRegistrar
{
    public async Task<Result<ColdWalletRegistration>> RegisterAsync(
        RegisterColdWalletCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Address))
            return Result.Failure<ColdWalletRegistration>(TreasuryColdWalletErrors.AddressRequired);

        var address = command.Address.Trim();

        // Format check before anything else. It proves only that the string is a structurally valid address
        // for the chain, never that it is the address the operator meant — but it is the difference between
        // catching a typo now and discovering it when the first sweep leaves.
        if (addressEncoders.Supports(command.Chain) && !addressEncoders.For(command.Chain).IsValidAddress(address))
            return Result.Failure<ColdWalletRegistration>(TreasuryColdWalletErrors.InvalidAddress);

        var now = timeProvider.GetUtcNow();
        var existing = await repository.FindByAddressAsync(command.Chain, address, cancellationToken);
        if (existing is not null)
        {
            // Same address, different class of funds: refuse. One address cannot be both the clean and the
            // quarantine destination without defeating the point of having two.
            if (existing.Kind != command.Kind)
                return Result.Failure<ColdWalletRegistration>(TreasuryColdWalletErrors.AlreadyRegistered);

            var warnings = await ScreenAsync(existing, address, command.Chain, now, force: false, cancellationToken);
            if (command.Activate && !existing.IsActive)
                return await DesignateAsync(existing, now, warnings, cancellationToken);

            await repository.SaveChangesAsync(cancellationToken);
            return Result.Success(Describe(existing, replacedAddress: null, warnings));
        }

        var created = TreasuryColdWallet.Register(
            command.Chain, command.Kind, address, command.Label, activate: false, now);
        if (created.IsFailure)
            return Result.Failure<ColdWalletRegistration>(created.Error!);

        var wallet = created.Value;
        var screeningWarnings = await ScreenAsync(wallet, address, command.Chain, now, force: false, cancellationToken);

        await repository.AddAsync(wallet, cancellationToken);

        // Activating is a second, separate write so the new row exists before anything is displaced: a
        // failure part-way leaves the previous destination in place rather than a chain with none.
        return command.Activate
            ? await DesignateAsync(wallet, now, screeningWarnings, cancellationToken)
            : Result.Success(Describe(wallet, replacedAddress: null, screeningWarnings));
    }

    public async Task<Result<ColdWalletRegistration>> ActivateAsync(
        Guid walletId, CancellationToken cancellationToken = default)
    {
        var wallet = await repository.FindByIdAsync(walletId, cancellationToken);
        if (wallet is null)
            return Result.Failure<ColdWalletRegistration>(TreasuryColdWalletErrors.NotFound);

        return await DesignateAsync(wallet, timeProvider.GetUtcNow(), [], cancellationToken);
    }

    public async Task<Result<ColdWalletRegistration>> RetireAsync(
        Guid walletId, CancellationToken cancellationToken = default)
    {
        var wallet = await repository.FindByIdAsync(walletId, cancellationToken);
        if (wallet is null)
            return Result.Failure<ColdWalletRegistration>(TreasuryColdWalletErrors.NotFound);

        if (wallet.IsActive)
            return Result.Failure<ColdWalletRegistration>(TreasuryColdWalletErrors.CannotRetireActive);

        wallet.Retire(timeProvider.GetUtcNow());
        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success(Describe(wallet, replacedAddress: null, []));
    }

    public async Task<Result<ColdWalletRegistration>> ReScreenAsync(
        Guid walletId, CancellationToken cancellationToken = default)
    {
        var wallet = await repository.FindByIdAsync(walletId, cancellationToken);
        if (wallet is null)
            return Result.Failure<ColdWalletRegistration>(TreasuryColdWalletErrors.NotFound);

        var now = timeProvider.GetUtcNow();
        var warnings = await ScreenAsync(wallet, wallet.Address, wallet.Chain, now, force: true, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success(Describe(wallet, replacedAddress: null, warnings));
    }

    /// <summary>
    /// Points the chain's sweeps at this wallet. The wallet it replaces is retired first, inside one
    /// transaction: the filtered unique index allows a single active row per (chain, kind), so doing it in
    /// the other order would be refused by the database, and doing it in two transactions would leave a
    /// window with no destination at all.
    /// </summary>
    private async Task<Result<ColdWalletRegistration>> DesignateAsync(
        TreasuryColdWallet wallet, DateTimeOffset now, IReadOnlyList<string> warnings, CancellationToken cancellationToken)
    {
        var replaced = await repository.InTransactionAsync(async ct =>
        {
            var current = await repository.FindActiveAsync(wallet.Chain, wallet.Kind, ct);
            string? previousAddress = null;

            if (current is not null && current.Id != wallet.Id)
            {
                current.Retire(now);
                previousAddress = current.Address;
                await repository.SaveChangesAsync(ct);
            }

            wallet.Activate(now);
            await repository.SaveChangesAsync(ct);
            return previousAddress;
        }, cancellationToken);

        logger.LogWarning(
            "Cold collection wallet for {Chain}/{Kind} is now {Address} (replaced {Previous}).",
            wallet.Chain, wallet.Kind, wallet.Address, replaced ?? "none");

        return Result.Success(Describe(wallet, replaced, warnings));
    }

    /// <summary>
    /// Screens the address and snapshots the verdict onto the row. Returns operator-facing warnings; it
    /// never fails the operation — see <see cref="ITreasuryColdWalletRegistrar.RegisterAsync"/>.
    /// </summary>
    private async Task<IReadOnlyList<string>> ScreenAsync(
        TreasuryColdWallet wallet, string address, Chain chain, DateTimeOffset now, bool force,
        CancellationToken cancellationToken)
    {
        if (!force && !screeningOptions.Value.ScreenCollectionWallets)
            return [];

        // The provider is an OPTIONAL dependency so Treasury stays composable in a host that never registers
        // a collection wallet (§15.10). Unlike the settlement-wallet path this does not fail the call — the
        // verdict gates nothing here, and refusing to record a destination because a vendor is absent would
        // stop an operator fixing a chain that has none. It is surfaced as a warning instead of being silent.
        if (screening is null)
        {
            logger.LogWarning(
                "Cold collection wallet {Address} on {Chain} was not screened: no screening provider is composed in this host.",
                address, chain);
            return ["Address was not screened: no screening provider is composed in this host."];
        }

        var verdict = force
            ? await screening.ReScreenAsync(chain, address, ScreeningPurpose.ColdCollectionWallet, cancellationToken)
            : await screening.ScreenAsync(chain, address, ScreeningPurpose.ColdCollectionWallet, cancellationToken);

        wallet.RecordScreening(
            verdict.ScreeningId, verdict.Decision.ToString(), verdict.Score, verdict.ScreenedAt, now);

        return WarningsFor(verdict, wallet.Kind);
    }

    /// <summary>
    /// Notes about an accepted address. A flagged <see cref="ColdWalletKind.Danger"/> wallet is not remarked
    /// on — it is the quarantine address, so a poor score there is the control working, and an alarm nobody
    /// should act on is how real alarms get ignored.
    /// </summary>
    private static IReadOnlyList<string> WarningsFor(ScreeningVerdict verdict, ColdWalletKind kind) =>
        (verdict.Decision, kind) switch
        {
            (ScreeningDecision.Allow, _) => [],
            (_, ColdWalletKind.Danger) => [],
            (ScreeningDecision.Block, _) =>
            [
                $"Screening flagged this address as high risk (score {verdict.Score}). It was accepted, but a "
                + "clean-collection address is not normally expected to be flagged — verify it before activating it.",
            ],
            (ScreeningDecision.Review, _) =>
            [
                $"Screening flagged this address for review (score {verdict.Score}). It was accepted and recorded.",
            ],
            _ =>
            [
                "Screening could not obtain a verdict for this address. It was accepted and recorded as unscreened.",
            ],
        };

    private static ColdWalletRegistration Describe(
        TreasuryColdWallet wallet, string? replacedAddress, IReadOnlyList<string> warnings) => new(
            wallet.Id, wallet.Chain, wallet.Kind, wallet.Address, wallet.Label, wallet.Status,
            wallet.ScreeningDecision, wallet.ScreeningScore, wallet.ScreeningId, replacedAddress, warnings);
}
