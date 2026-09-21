using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Application;

/// <summary>
/// The one and only time the secrets are ever readable. <see cref="ApiSecret"/> is the bearer secret;
/// <see cref="SigningSecret"/> is the request/callback HMAC key the merchant signs with. Neither is stored
/// recoverably except the signing secret's encrypted-at-rest form; callers must not log either. If the
/// merchant loses them they rotate — the bearer secret has no recovery, that is the point.
/// </summary>
public sealed record MerchantRegistrationResult(
    Guid MerchantId, string MerchantCode, string ApiKey, string ApiSecret, string SigningSecret);

/// <summary>BO read model — deliberately richer than the public <c>IMerchantDirectory</c> Contract (which
/// never exposes credential presence), since this is for staff, not other modules.</summary>
public sealed record MerchantAdminView(
    Guid MerchantId,
    string MerchantCode,
    string Name,
    string Status,
    int SettlementDelayDays,
    bool RequiresPayoutApproval,
    DateTimeOffset CreatedAt,
    bool HasActiveCredential,
    IReadOnlyList<string> AllowedIps,
    IReadOnlyList<MerchantSettlementWalletView> SettlementWallets,
    string? ContactEmail = null,
    string? Remark = null,
    string SettlementMode = "Manual");

/// <summary>
/// A merchant's whitelisted cash-out destination for a chain, for staff read-back, carrying the STANDING
/// screening verdict.
///
/// <para>Read from stored evidence only — this never calls the provider, so opening a merchant costs no
/// quota and cannot be slowed down or broken by a vendor outage.</para>
///
/// <para>Every screening field is null when the address has never been screened, which is deliberately
/// different from a verdict of "Unavailable" (asked, no answer). <paramref name="ScreenedAt"/> matters as
/// much as the decision: a verdict is a snapshot, and an old one on a high-value destination is itself
/// worth seeing.</para>
/// </summary>
public sealed record MerchantSettlementWalletView(
    string Chain,
    string Address,
    Guid WalletId = default,
    string? Label = null,

    /// <summary><c>Active</c> is the address this chain's cash-outs are paid to; <c>Retired</c> is on file
    /// but not in use. Several may be listed per chain — exactly one of them is Active.</summary>
    string Status = nameof(SettlementWalletStatus.Active),
    string? ScreeningDecision = null,
    int? ScreeningScore = null,
    Guid? ScreeningId = null,
    DateTimeOffset? ScreenedAt = null);

/// <summary>
/// The outcome of whitelisting a settlement wallet, including what address screening made of it.
/// <paramref name="ScreeningDecision"/> is null when settlement screening is switched off — distinct from
/// "Unavailable", which means we asked and could not get an answer. <paramref name="Warnings"/> is empty
/// for a clean or unscreened address, so a UI shows nothing in the normal case.
/// </summary>
public sealed record SettlementWalletResult(
    Guid MerchantId,
    Chain Chain,
    string Address,

    /// <summary>The whitelisted wallet's id — what the activate/retire actions address, now that a merchant
    /// may keep several addresses on file per chain.</summary>
    Guid WalletId,
    string? Label,

    /// <summary><c>Active</c> (this is where the chain's cash-outs are paid) or <c>Retired</c> (on file,
    /// not in use).</summary>
    string Status,
    string? ScreeningDecision,
    int? ScreeningScore,

    /// <summary>The evidence row this verdict came from, so a caller can link straight to the full record
    /// (indicators, the provider payload, the policy in force) instead of re-screening to see why. Null when
    /// the address was not screened. An opaque reference into <c>compliance.AddressScreening</c>,
    /// deliberately not a foreign key (§4.5) — the same shape the withdrawal row carries.</summary>
    Guid? ScreeningId,
    IReadOnlyList<string> Warnings);

public interface IMerchantRegistrar
{
    /// <summary>Registers a merchant under a backend-minted code — <c>"ME"</c> + a zero-padded 5-digit
    /// sequence (<c>ME00001</c>, <c>ME00002</c>, …). Callers never supply or influence the code; see the
    /// implementation for how collisions against the sequence are handled.</summary>
    Task<Result<MerchantRegistrationResult>> RegisterAsync(
        string name,
        string? callbackUrl,
        CancellationToken cancellationToken = default,
        string? contactEmail = null,
        string? remark = null,
        Domain.SettlementMode settlementMode = Domain.SettlementMode.Manual,
        int settlementDelayDays = 0);

    /// <summary>Updates the staff-facing profile fields (contact email, settlement mode, remark). Every
    /// parameter is optional — a caller sends only what changed; an omitted (null) field is left unchanged,
    /// an explicit empty string clears it. Write-only: returns success/failure only, no read-back shape.</summary>
    Task<Result> SetProfileAsync(
        Guid merchantId, string? contactEmail, Domain.SettlementMode? settlementMode, string? remark,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unfreezes a <c>Frozen</c> merchant back to <c>Active</c>. A newly-registered merchant is already
    /// <c>Active</c> (no separate approval step) — this exists purely to reverse <see cref="FreezeAsync"/>.
    /// </summary>
    Task<Result> ActivateAsync(Guid merchantId, CancellationToken cancellationToken = default);

    /// <summary>Admin risk-hold — blocks all transacting for the merchant. Reversible: <see cref="ActivateAsync"/>
    /// unfreezes it. Matches APIGateway's merchant status toggle, expressed against this project's status enum.</summary>
    Task<Result> FreezeAsync(Guid merchantId, CancellationToken cancellationToken = default);

    /// <summary>Admin action — blocks all transacting, same effect as <see cref="FreezeAsync"/>. Reversible:
    /// <see cref="ActivateAsync"/>/<see cref="FreezeAsync"/> can move a Closed merchant back out — "Closed" is
    /// a status like any other, not a delete. Every other business operation independently keeps rejecting a
    /// Closed merchant regardless of this.</summary>
    Task<Result> CloseAsync(Guid merchantId, CancellationToken cancellationToken = default);

    /// <summary>Sets the merchant's settlement period (T+N) in whole days (0 = T+0). Gates the withdrawable
    /// balance for BOTH user payouts and the merchant cash-out. The domain validates the 0–30 range.</summary>
    Task<Result> SetSettlementDelayAsync(Guid merchantId, int days, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns the merchant's OWN payout-approval stage on or off. When on, a user payout this merchant raises
    /// waits in <c>PendingMerchantApproval</c> for the merchant's approver before the platform evaluates it —
    /// regardless of whether it arrived over the HMAC API or the portal, because this is merchant policy, not
    /// a property of the caller.
    ///
    /// <para>Defaults to off, so no existing integration changes behaviour until staff switch it on. Enabling
    /// it changes where that merchant's money stops, so a UI should confirm the impact first.</para>
    /// </summary>
    Task<Result> SetRequiresPayoutApprovalAsync(Guid merchantId, bool required, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whitelists an address and makes it the merchant's cash-out destination for a chain in one step —
    /// what this action has always done. Equivalent to <see cref="AddSettlementWalletAsync"/> with
    /// <c>activate: true</c>.
    /// </summary>
    Task<Result<SettlementWalletResult>> SetSettlementWalletAsync(
        Guid merchantId, Chain chain, string address, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whitelists a settlement (cash-out) address — the destination of a Merchant Withdrawal, never
    /// client-supplied (§10). A merchant may keep several on file per chain; only the active one is paid.
    ///
    /// <para>When settlement screening is enabled the address is checked first. <b>Whitelisting is never
    /// refused</b> — staff may keep an address on file whatever a vendor says about it — but making a
    /// directly designated address the destination is, because every one of that merchant's earnings would
    /// then be paid to it. The wallet is saved either way and the verdict is returned, so the refusal is a
    /// recorded, reversible decision rather than lost work.</para>
    /// </summary>
    Task<Result<SettlementWalletResult>> AddSettlementWalletAsync(
        Guid merchantId, Chain chain, string address, string? label, bool activate,
        CancellationToken cancellationToken = default);

    /// <summary>Makes an already-whitelisted address the chain's cash-out destination, retiring the one it
    /// replaces. Re-screened at this point, because this is where it starts receiving earnings.</summary>
    Task<Result<SettlementWalletResult>> ActivateSettlementWalletAsync(
        Guid merchantId, Guid walletId, CancellationToken cancellationToken = default);

    /// <summary>Takes an address out of use without deleting it. Refused for the active one — activate a
    /// replacement instead, which retires it as part of the same change.</summary>
    Task<Result<SettlementWalletResult>> RetireSettlementWalletAsync(
        Guid merchantId, Guid walletId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The "change password" equivalent for a merchant: merchants don't log in here, they authenticate by
    /// API credential, so rotating means revoking the current active credential and issuing a fresh one —
    /// same effect (old secret stops working immediately) via this project's real mechanism, matching
    /// APIGateway's <c>RegenerateKey</c>.
    /// </summary>
    Task<Result<MerchantRegistrationResult>> RotateCredentialAsync(Guid merchantId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the merchant's IP allowlist. IP format validation is the caller's job (the host
    /// edge) — this only persists and diffs, matching APIGateway's <c>UpdateAllowedIps</c>.</summary>
    Task<Result<AllowedIpsChange>> UpdateAllowedIpsAsync(
        Guid merchantId, IReadOnlyCollection<string> validIps, CancellationToken cancellationToken = default);

    Task<Result<MerchantAdminView>> GetAsync(Guid merchantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The merchant code <see cref="RegisterAsync"/> will most likely mint next (e.g. "ME00042") — a preview
    /// for the create-merchant UI to display, computed the same way the real generator does. This is NOT a
    /// reservation: a concurrent registration can still claim this exact number first, in which case
    /// <see cref="RegisterAsync"/>'s own collision retry silently rolls to the next one, same as always.
    /// </summary>
    Task<string> PreviewNextMerchantCodeAsync(CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<MerchantAdminView> Items, int TotalCount)> ListAsync(
        int page, int pageSize, CancellationToken cancellationToken = default);
}

public sealed class MerchantRegistrar(
    IMerchantRepository repository,
    IApiCredentialGenerator generator,
    IApiSecretHasher hasher,
    ISecretCipher secretCipher,
    TimeProvider timeProvider,
    IAddressScreeningService? screening,
    IOptions<MerchantScreeningOptions> screeningOptions) : IMerchantRegistrar
{
    /// <summary>Bounded retries for a generated-code collision — see <see cref="RegisterAsync"/>.</summary>
    private const int MaxCodeGenerationAttempts = 8;

    public async Task<Result<MerchantRegistrationResult>> RegisterAsync(
        string name,
        string? callbackUrl,
        CancellationToken cancellationToken = default,
        string? contactEmail = null,
        string? remark = null,
        Domain.SettlementMode settlementMode = Domain.SettlementMode.Manual,
        int settlementDelayDays = 0)
    {
        for (var attempt = 1; attempt <= MaxCodeGenerationAttempts; attempt++)
        {
            var sequence = await repository.GetNextMerchantCodeSequenceAsync(cancellationToken);
            var candidateCode = $"ME{sequence:D5}";

            var merchantResult = Domain.Merchant.Create(
                candidateCode, name, callbackUrl, timeProvider, contactEmail, remark, settlementMode);
            if (merchantResult.IsFailure)
                return Result.Failure<MerchantRegistrationResult>(merchantResult.Error!);

            var merchant = merchantResult.Value;

            if (settlementDelayDays != 0)
            {
                var delayResult = merchant.SetSettlementDelay(settlementDelayDays, timeProvider.GetUtcNow());
                if (delayResult.IsFailure)
                    return Result.Failure<MerchantRegistrationResult>(delayResult.Error!);
            }

            // Pre-check for a friendly, fast retry. The UNIQUE index on MerchantCode remains the real
            // arbiter — two concurrent registrations reading the same "next" sequence will still collide
            // there (caught below), by design; either check losing just means this candidate is taken,
            // so move on to the next one.
            if (await repository.CodeExistsAsync(merchant.MerchantCode, cancellationToken))
                continue;

            var credential = generator.Generate();
            var secretHash = hasher.Hash(credential.Secret);
            var signingSecretCipher = secretCipher.Protect(credential.SigningSecret);

            var issueResult = merchant.IssueCredential(
                credential.ApiKey, secretHash, hasher.CurrentVersion, signingSecretCipher, timeProvider.GetUtcNow());

            if (issueResult.IsFailure)
                return Result.Failure<MerchantRegistrationResult>(issueResult.Error!);

            repository.Add(merchant);

            // Lost the insert race for this candidate to a concurrent registration that read the same
            // "next" sequence? TrySaveNewMerchantAsync detaches the doomed insert and returns false —
            // retry with a fresh candidate rather than surfacing it.
            if (!await repository.TrySaveNewMerchantAsync(merchant, cancellationToken))
                continue;

            return Result.Success(new MerchantRegistrationResult(
                merchant.Id, merchant.MerchantCode, credential.ApiKey, credential.Secret, credential.SigningSecret));
        }

        return Result.Failure<MerchantRegistrationResult>(MerchantErrors.CodeAlreadyExists);
    }

    public async Task<string> PreviewNextMerchantCodeAsync(CancellationToken cancellationToken = default)
    {
        var sequence = await repository.GetNextMerchantCodeSequenceAsync(cancellationToken);
        return $"ME{sequence:D5}";
    }

    public async Task<Result> ActivateAsync(Guid merchantId, CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure(MerchantErrors.NotFound);

        var activateResult = merchant.Activate(timeProvider.GetUtcNow());
        if (activateResult.IsFailure)
            return activateResult;

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> FreezeAsync(Guid merchantId, CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure(MerchantErrors.NotFound);

        var result = merchant.Freeze(timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> CloseAsync(Guid merchantId, CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure(MerchantErrors.NotFound);

        var result = merchant.Close(timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> SetProfileAsync(
        Guid merchantId, string? contactEmail, Domain.SettlementMode? settlementMode, string? remark,
        CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure(MerchantErrors.NotFound);

        var result = merchant.SetProfile(contactEmail, settlementMode, remark, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> SetRequiresPayoutApprovalAsync(Guid merchantId, bool required, CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure(MerchantErrors.NotFound);

        var result = merchant.SetRequiresPayoutApproval(required, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> SetSettlementDelayAsync(Guid merchantId, int days, CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure(MerchantErrors.NotFound);

        var result = merchant.SetSettlementDelay(days, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// <para><b>Why this screens synchronously when a payout does not.</b> A payout is screened by a worker
    /// because the provider allows roughly one call per second and payouts arrive in bursts. Whitelisting a
    /// settlement wallet is a rare, deliberate staff action — one call per merchant per chain — so there is
    /// no burst to pace and no reason to make an operator wait for a queue.</para>
    ///
    /// <para><b>And why only a Block refuses here.</b> A payout runs unattended, so anything short of a clean
    /// verdict parks it for a human. This runs WITH a human already exercising judgement, who may hold
    /// context the provider does not — so a flagged or unobtainable verdict is surfaced to them rather than
    /// overriding them. A Block is the exception because a sanctions hit is a legal fact, and this is the
    /// destination every one of that merchant's earnings is paid to.</para>
    /// </summary>
    public Task<Result<SettlementWalletResult>> SetSettlementWalletAsync(
        Guid merchantId, Chain chain, string address, CancellationToken cancellationToken = default) =>
        AddSettlementWalletAsync(merchantId, chain, address, label: null, activate: true, cancellationToken);

    public async Task<Result<SettlementWalletResult>> AddSettlementWalletAsync(
        Guid merchantId, Chain chain, string address, string? label, bool activate,
        CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure<SettlementWalletResult>(MerchantErrors.NotFound);

        if (string.IsNullOrWhiteSpace(address))
            return Result.Failure<SettlementWalletResult>(MerchantErrors.SettlementAddressRequired);

        var trimmed = address.Trim();

        // Screen BEFORE mutating, so a refused activation leaves the existing whitelist untouched rather
        // than clearing it — losing a good settlement wallet to a failed replacement would halt that
        // merchant's cash-outs for a reason that has nothing to do with the wallet already on file.
        var screened = await ScreenSettlementWalletAsync(chain, trimmed, force: false, cancellationToken);
        if (screened.IsFailure)
            return Result.Failure<SettlementWalletResult>(screened.Error!);

        var verdict = screened.Value;

        // Whitelisting is never refused — staff may keep an address on file whatever a vendor says about it.
        // MAKING IT THE DESTINATION is the act that moves money, and a direct sanctions designation stops
        // that: every one of this merchant's earnings would be paid to it. The wallet is still saved, so the
        // decision is recorded and reversible by re-screening rather than lost.
        var blockActivation = activate
            && verdict?.Decision == ScreeningDecision.Block
            && screeningOptions.Value.BlockActivationOnScreeningBlock;

        var now = timeProvider.GetUtcNow();
        var result = merchant.AddSettlementWallet(chain, trimmed, label, activate: false, now);
        if (result.IsFailure)
            return Result.Failure<SettlementWalletResult>(result.Error!);

        var wallet = result.Value;
        await repository.SaveChangesAsync(cancellationToken);

        if (blockActivation)
            return Result.Failure<SettlementWalletResult>(MerchantErrors.SettlementWalletBlocked);

        if (activate)
        {
            var designated = await DesignateAsync(merchant, wallet.Id, now, cancellationToken);
            if (designated.IsFailure)
                return Result.Failure<SettlementWalletResult>(designated.Error!);
        }

        return Result.Success(Describe(merchantId, wallet, verdict));
    }

    public async Task<Result<SettlementWalletResult>> ActivateSettlementWalletAsync(
        Guid merchantId, Guid walletId, CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure<SettlementWalletResult>(MerchantErrors.NotFound);

        var wallet = merchant.SettlementWallets.SingleOrDefault(w => w.Id == walletId);
        if (wallet is null)
            return Result.Failure<SettlementWalletResult>(MerchantErrors.SettlementWalletNotFound);

        // Re-screened at the moment of activation rather than trusting the verdict from when it was added:
        // an address clean on the day it was whitelisted can be designated months later, and this is the
        // point where it would start receiving earnings. A still-fresh verdict costs no provider call.
        var screened = await ScreenSettlementWalletAsync(
            wallet.Chain, wallet.Address, force: false, cancellationToken);
        if (screened.IsFailure)
            return Result.Failure<SettlementWalletResult>(screened.Error!);

        if (screened.Value?.Decision == ScreeningDecision.Block
            && screeningOptions.Value.BlockActivationOnScreeningBlock)
            return Result.Failure<SettlementWalletResult>(MerchantErrors.SettlementWalletBlocked);

        var designated = await DesignateAsync(merchant, walletId, timeProvider.GetUtcNow(), cancellationToken);
        if (designated.IsFailure)
            return Result.Failure<SettlementWalletResult>(designated.Error!);

        return Result.Success(Describe(merchantId, designated.Value, screened.Value));
    }

    /// <summary>
    /// Switches which wallet a chain's cash-outs are paid to.
    ///
    /// <para>The previous one is retired and <b>saved first</b>, then the replacement is activated and saved,
    /// both inside one transaction. Only one Active row per (merchant, chain) is allowed and EF picks its own
    /// statement order, so doing it in a single save fails whenever it happens to send the activate first —
    /// which is intermittent, and therefore worse than failing every time. Two separate transactions would be
    /// worse still: a crash between them leaves the merchant with no destination and every cash-out refused.</para>
    /// </summary>
    private async Task<Result<Domain.MerchantSettlementWallet>> DesignateAsync(
        Domain.Merchant merchant, Guid walletId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var retired = merchant.RetireOtherSettlementWallets(walletId, now);
        if (retired.IsFailure)
            return retired;

        return await repository.InTransactionAsync(async ct =>
        {
            await repository.SaveChangesAsync(ct);

            var activated = merchant.ActivateSettlementWallet(walletId, now);
            if (activated.IsSuccess)
                await repository.SaveChangesAsync(ct);

            return activated;
        }, cancellationToken);
    }

    public async Task<Result<SettlementWalletResult>> RetireSettlementWalletAsync(
        Guid merchantId, Guid walletId, CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure<SettlementWalletResult>(MerchantErrors.NotFound);

        var result = merchant.RetireSettlementWallet(walletId, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return Result.Failure<SettlementWalletResult>(result.Error!);

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success(Describe(merchantId, result.Value, verdict: null));
    }

    /// <summary>
    /// Screens a settlement address when the module is configured to. Returns null when screening is off —
    /// deliberately distinct from <c>Unavailable</c>, which means we asked and could not get an answer.
    /// </summary>
    private async Task<Result<ScreeningVerdict?>> ScreenSettlementWalletAsync(
        Chain chain, string address, bool force, CancellationToken cancellationToken)
    {
        if (!screeningOptions.Value.ScreenSettlementWallets)
            return Result.Success<ScreeningVerdict?>(null);

        // The screening provider is an OPTIONAL dependency, so this module stays composable on its own — a
        // host that never whitelists settlement wallets (the merchant portal) should not be forced to
        // compose Compliance just to boot (§15.10). But "configured to screen, with nothing to screen with"
        // is a misconfiguration that must never degrade into silently skipping the check, so it fails loudly
        // rather than saving an unscreened wallet that looks screened.
        if (screening is null)
        {
            return Result.Failure<ScreeningVerdict?>(Error.Failure(
                "merchant.screening_not_composed",
                "Settlement-wallet screening is enabled but no screening provider is registered in this host."));
        }

        var verdict = force
            ? await screening.ReScreenAsync(chain, address, ScreeningPurpose.SettlementWallet, cancellationToken)
            : await screening.ScreenAsync(chain, address, ScreeningPurpose.SettlementWallet, cancellationToken);

        return Result.Success<ScreeningVerdict?>(verdict);
    }

    private static SettlementWalletResult Describe(
        Guid merchantId, Domain.MerchantSettlementWallet wallet, ScreeningVerdict? verdict) => new(
        merchantId, wallet.Chain, wallet.Address, wallet.Id, wallet.Label, wallet.Status.ToString(),
        verdict?.Decision.ToString(), verdict?.Score, verdict?.ScreeningId, WarningsFor(verdict));

    /// <summary>Operator-facing notes about an accepted-but-not-clean address. Empty when screening is off or
    /// the address came back clean — an empty list is the normal case and must stay silent.</summary>
    private static IReadOnlyList<string> WarningsFor(ScreeningVerdict? verdict) => verdict?.Decision switch
    {
        ScreeningDecision.Review =>
        [
            $"Address screening flagged this wallet: {verdict.RiskLevel} (score {verdict.Score}). "
            + $"Accepted because a staff member is saving it. Indicators: {string.Join(", ", verdict.Reasons)}.",
        ],
        ScreeningDecision.Unavailable =>
        [
            "Address screening could not reach the provider, so this wallet was saved unscreened. "
            + "Re-screen it once the provider is available.",
        ],
        _ => [],
    };

    public async Task<Result<MerchantRegistrationResult>> RotateCredentialAsync(
        Guid merchantId, CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure<MerchantRegistrationResult>(MerchantErrors.NotFound);

        var now = timeProvider.GetUtcNow();

        // Revoke every currently-active credential — old raw secrets stop working immediately, the same
        // guarantee a password change gives, even though a merchant may (rarely) hold more than one.
        foreach (var active in merchant.Credentials.Where(c => c.IsActive).ToList())
        {
            var revokeResult = merchant.RevokeCredential(active.Id, now);
            if (revokeResult.IsFailure)
                return Result.Failure<MerchantRegistrationResult>(revokeResult.Error!);
        }

        var credential = generator.Generate();
        var secretHash = hasher.Hash(credential.Secret);
        var signingSecretCipher = secretCipher.Protect(credential.SigningSecret);

        var issueResult = merchant.IssueCredential(credential.ApiKey, secretHash, hasher.CurrentVersion, signingSecretCipher, now);
        if (issueResult.IsFailure)
            return Result.Failure<MerchantRegistrationResult>(issueResult.Error!);

        await repository.SaveChangesAsync(cancellationToken);

        return Result.Success(new MerchantRegistrationResult(
            merchant.Id, merchant.MerchantCode, credential.ApiKey, credential.Secret, credential.SigningSecret));
    }

    public async Task<Result<AllowedIpsChange>> UpdateAllowedIpsAsync(
        Guid merchantId, IReadOnlyCollection<string> validIps, CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure<AllowedIpsChange>(MerchantErrors.NotFound);

        var result = merchant.UpdateAllowedIps(validIps, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<Result<MerchantAdminView>> GetAsync(Guid merchantId, CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
        {
            return Result.Failure<MerchantAdminView>(MerchantErrors.NotFound);
        }

        // Stored verdicts only. A merchant has at most one settlement wallet per chain, so this is a
        // handful of indexed reads, and no provider call means opening a merchant never spends quota.
        // The list view deliberately does NOT do this: its query does not load settlement wallets at all,
        // and adding a lookup per row would put a per-page cost on a screen that shows none of it.
        var verdicts = new Dictionary<string, ScreeningVerdict>(StringComparer.OrdinalIgnoreCase);
        if (screening is not null)
        {
            foreach (var wallet in merchant.SettlementWallets)
            {
                var latest = await screening.FindLatestAsync(wallet.Chain, wallet.Address, cancellationToken);
                if (latest is not null)
                {
                    verdicts[$"{wallet.Chain}:{wallet.Address}"] = latest;
                }
            }
        }

        return Result.Success(ToView(merchant, verdicts));
    }

    public async Task<(IReadOnlyList<MerchantAdminView> Items, int TotalCount)> ListAsync(
        int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var (items, total) = await repository.GetPagedAsync(page, pageSize, cancellationToken);
        // No verdict map: GetPagedAsync does not load settlement wallets, so the list view has none to
        // annotate. The per-merchant read is where screening status belongs.
        return ([.. items.Select(m => ToView(m))], total);
    }

    /// <summary>Projects one wallet, attaching the standing verdict when one has been recorded. A wallet
    /// with no screening history keeps every screening field null, which reads as "not screened" rather
    /// than as a clean result.</summary>
    private static MerchantSettlementWalletView ToWalletView(
        Domain.MerchantSettlementWallet wallet, IReadOnlyDictionary<string, ScreeningVerdict>? verdicts)
    {
        ScreeningVerdict? verdict = null;
        if (verdicts is not null && verdicts.TryGetValue($"{wallet.Chain}:{wallet.Address}", out var found))
        {
            verdict = found;
        }

        return new MerchantSettlementWalletView(
            wallet.Chain.ToString(), wallet.Address, wallet.Id, wallet.Label, wallet.Status.ToString(),
            verdict?.Decision.ToString(), verdict?.Score, verdict?.ScreeningId, verdict?.ScreenedAt);
    }

    private static MerchantAdminView ToView(
        Domain.Merchant merchant, IReadOnlyDictionary<string, ScreeningVerdict>? verdicts = null) => new(
        merchant.Id,
        merchant.MerchantCode,
        merchant.Name,
        merchant.Status.ToString(),
        merchant.SettlementDelayDays,
        merchant.RequiresPayoutApproval,
        merchant.CreatedAt,
        merchant.Credentials.Any(c => c.IsActive),
        merchant.Configuration.AllowedIps,
        [.. merchant.SettlementWallets.Select(w => ToWalletView(w, verdicts))],
        merchant.ContactEmail,
        merchant.Remark,
        merchant.SettlementMode.ToString());
}
