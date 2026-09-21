using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing address-screening surface — the back-office view of the AML evidence trail, plus the one
/// action an operator needs against it.
///
/// <para><b>The list is a pure read over an append-only table</b> (§4.7 — this host runs no screening
/// worker). It cannot alter a past decision, because the trail's whole value is that it cannot be: a
/// payout's verdict has to stay explainable months later, exactly as a ledger entry does (§14).</para>
///
/// <para><b>Re-screening appends, it never edits.</b> It is the only write here and it spends provider
/// quota, so it carries its own permission code rather than riding on the read.</para>
///
/// <para>No money and no keys pass through this endpoint (§10) — screening reads a public address and
/// stores a third party's opinion about it.</para>
/// </summary>
public static class OpsComplianceEndpoints
{
    public static void MapOpsComplianceApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ops/compliance/screenings", ListAsync)
            .RequirePermission(OpsPermissions.Compliance.View);

        app.MapGet("/api/v1/ops/compliance/screenings/{screeningId:guid}", DetailAsync)
            .RequirePermission(OpsPermissions.Compliance.View);

        app.MapPost("/api/v1/ops/compliance/screenings/re-screen", ReScreenAsync)
            .RequirePermission(OpsPermissions.Compliance.Manage);

        app.MapPost("/api/v1/ops/compliance/deposit-addresses/screen", ScreenDepositAddressesAsync)
            .RequirePermission(OpsPermissions.Compliance.Manage);

        // What is true NOW: one row per address at its latest verdict. The screenings list above is the
        // history; a work queue belongs here.
        app.MapGet("/api/v1/ops/compliance/addresses", CurrentAddressesAsync)
            .RequirePermission(OpsPermissions.Compliance.View);

        // A read, sent as POST only because a page of addresses does not fit in a query string.
        app.MapPost("/api/v1/ops/compliance/screenings/latest", LatestForAddressesAsync)
            .RequirePermission(OpsPermissions.Compliance.View);

        // Reading the thresholds is a View right; changing them is not. A threshold change decides whether
        // money moves, so it sits behind the same code as spending quota.
        app.MapGet("/api/v1/ops/compliance/policy", GetPolicyAsync)
            .RequirePermission(OpsPermissions.Compliance.View);

        app.MapGet("/api/v1/ops/compliance/policy/history", GetPolicyHistoryAsync)
            .RequirePermission(OpsPermissions.Compliance.View);

        app.MapPut("/api/v1/ops/compliance/policy", UpdatePolicyAsync)
            .RequirePermission(OpsPermissions.Compliance.Manage);
    }

    /// <summary>
    /// The thresholds currently in force, plus the configured defaults beside them.
    ///
    /// <para><c>source</c> distinguishes "nobody has ever set this" from "someone set it to exactly the
    /// default". They look identical otherwise, and only one of them is a question worth asking.</para>
    /// </summary>
    private static async Task<IResult> GetPolicyAsync(
        IScreeningPolicyService policy, HttpContext http)
    {
        var current = await policy.GetAsync(http.RequestAborted);

        return OpsResults.Ok(new
        {
            current = ToPolicy(current),
            configuredDefaults = ToPolicy(policy.GetConfiguredDefaults()),

            // What this API deliberately does NOT control, named so a settings screen can say so rather
            // than leaving an operator hunting for a switch that is not there.
            notEditableHere = new
            {
                reason =
                    "Master switches stay in configuration. These decide whether a control exists; the "
                    + "thresholds above decide how it is calibrated. Changing them takes a deployment, so a "
                    + "stolen session cannot silently switch off a control that holds money.",
                keys = new[]
                {
                    "Compliance:Enabled",
                    "Withdrawal:Screening:Enabled",
                    "Merchant:Screening:ScreenSettlementWallets",
                    "Merchant:Screening:RescreenSettlementWallets",
                    "Wallet:Screening:Enabled",
                },
            },
        });
    }

    private static async Task<IResult> GetPolicyHistoryAsync(
        IScreeningPolicyService policy, HttpContext http, int limit = 50)
    {
        var history = await policy.GetHistoryAsync(limit, http.RequestAborted);

        return OpsResults.Ok(new
        {
            items = history.Select(h => new
            {
                id = h.Id,
                blockScore = h.BlockScore,
                reviewScore = h.ReviewScore,
                cacheDays = h.CacheDays,
                indirectReviewMaxHops = h.IndirectReviewMaxHops,
                indirectReviewMinPercent = h.IndirectReviewMinPercent,
                addedIndicators = h.AddedIndicators,
                updatedBy = h.UpdatedBy,
                updatedAt = h.UpdatedAt,
                note = h.Note,
            }).ToList(),
        });
    }

    private sealed record PolicyRequest(
        int BlockScore,
        int ReviewScore,
        int CacheDays,
        int IndirectReviewMaxHops,
        decimal IndirectReviewMinPercent,
        string[]? AddedIndicators,
        string? Note);

    /// <summary>
    /// Saves new thresholds. Every field is required — a partial update on a policy screen invites changing
    /// one threshold while silently reverting another to whatever stale value the caller happened to hold.
    ///
    /// <para>Appends a version rather than overwriting one, so an old evidence row stays explainable against
    /// the policy it was actually judged under, and the trail of who calibrated the control survives.</para>
    /// </summary>
    private static async Task<IResult> UpdatePolicyAsync(
        PolicyRequest request, IScreeningPolicyService policy, HttpContext http)
    {
        // The acting staff member, from the already-validated session and never from the request body: a
        // compliance change that records an attribution the caller supplied is not an attribution. Same
        // helper every other mutating endpoint uses, so the identity cannot be derived differently here.
        var actor = AuditActor.From(http).Username;

        var result = await policy.UpdateAsync(
            new ScreeningPolicyUpdate(
                request.BlockScore,
                request.ReviewScore,
                request.CacheDays,
                request.IndirectReviewMaxHops,
                request.IndirectReviewMinPercent,
                request.AddedIndicators,
                request.Note),
            actor,
            http.RequestAborted);

        return result.IsFailure ? OpsResults.Fail(result.Error!) : OpsResults.Ok(ToPolicy(result.Value));
    }

    private static object ToPolicy(ScreeningPolicyView p) => new
    {
        blockScore = p.BlockScore,
        reviewScore = p.ReviewScore,
        cacheDays = p.CacheDays,

        // Zero disables the proximity rule. Say so in the payload rather than leaving a UI to infer it from
        // a magic number.
        indirectReviewMaxHops = p.IndirectReviewMaxHops,
        indirectReviewMinPercent = p.IndirectReviewMinPercent,
        proximityRuleEnabled = p.IndirectReviewMaxHops > 0,

        alwaysBlockIndicators = p.AlwaysBlockIndicators,

        // Only these may be removed again. The rest ships with the platform and is not deletable here,
        // because a sanctions override is exactly the rule that should not come off in a web form.
        editableIndicators = p.EditableIndicators,

        source = p.Source,
        updatedBy = p.UpdatedBy,
        updatedAt = p.UpdatedAt,
        note = p.Note,
    };

    /// <summary>
    /// Sweeps the platform's own funded deposit addresses on demand.
    ///
    /// <para>This is the inbound control, in the only form available. A transfer cannot be screened while it
    /// is in flight, and once it lands it is credited and never reversed — a frozen merchant's deposits
    /// still credit the ledger (§14). What can be watched is the other side of the graph: a provider scores
    /// an address from its history, so funds arriving from a bad counterparty raise the score of OUR
    /// address.</para>
    ///
    /// <para>It records and flags. Nothing is reversed, withheld or disabled — by the time an address looks
    /// bad the money has already reached a merchant's balance, so an automatic reaction would mean clawing
    /// back funds on a vendor's say-so.</para>
    ///
    /// <para>Runs regardless of whether the SCHEDULED pass is switched on, so staff can sweep without
    /// committing to a standing spend. It still honours the per-pass cap: a manual run costs exactly the
    /// same quota as a scheduled one, and the payout gate shares that budget.</para>
    /// </summary>
    private static async Task<IResult> ScreenDepositAddressesAsync(
        DepositAddressScreeningService depositScreening, HttpContext http)
    {
        var result = await depositScreening.ScreenOnceAsync(force: true, http.RequestAborted);

        return OpsResults.Ok(new
        {
            // How many were due, versus how many were actually done. The two diverging means the per-pass
            // cap is biting and a backlog is building.
            candidates = result.Candidates,
            screened = result.Screened,
            flagged = result.Flagged,
            deferred = Math.Max(0, result.Candidates - result.Screened),
        });
    }

    private static async Task<IResult> ListAsync(
        IAddressScreeningDirectory screenings,
        HttpContext http,
        string? chain = null,
        string? decision = null,
        string? purpose = null,
        string? address = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        int page = 1,
        int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;

        if (!TryParseChain(chain, out var chainFilter, out var chainError))
        {
            return OpsResults.Bad(OpsErrorCodes.InvalidChain, chainError!);
        }

        if (!TryParseDecision(decision, out var decisionFilter, out var decisionError))
        {
            return decisionError!;
        }

        if (!TryParsePurpose(purpose, out var purposeFilter, out var purposeError))
        {
            return purposeError!;
        }

        var filter = new ScreeningAdminFilter(
            chainFilter, decisionFilter, purposeFilter, address, fromDate, toDate);

        var (items, total) = await screenings.SearchAsync(filter, page, pageSize, http.RequestAborted);
        var counts = await screenings.GetDecisionCountsAsync(chainFilter, http.RequestAborted);

        // Every decision is present even at zero, so a UI renders a stable set of counters rather than
        // tiles that appear and vanish as the data changes.
        var summary = Enum.GetValues<ScreeningDecision>()
            .ToDictionary(d => d.ToString(), d => counts.TryGetValue(d, out var n) ? n : 0);

        return OpsResults.Ok(new
        {
            page,
            pageSize,
            totalCount = total,
            summary,
            items = items.Select(ToRow).ToList(),
        });
    }

    private static async Task<IResult> DetailAsync(
        Guid screeningId, IAddressScreeningDirectory screenings, HttpContext http)
    {
        var detail = await screenings.FindByIdAsync(screeningId, http.RequestAborted);
        if (detail is null)
        {
            return OpsResults.NotFound(OpsErrorCodes.NotFound, "No such screening.");
        }

        // The verbatim provider payload, included ONLY here. It is what settles a dispute about what the
        // vendor actually said, as opposed to what we parsed out of it. Returned as the stored string
        // rather than re-serialised, so it stays byte-identical to the evidence.
        return OpsResults.Ok(new { screening = ToRow(detail.Row), rawResponse = detail.RawResponse });
    }

    private sealed record ReScreenRequest(string? Chain, string? Address, string? Purpose);

    private static async Task<IResult> ReScreenAsync(
        ReScreenRequest request, IAddressScreeningService screening, HttpContext http)
    {
        if (!TryParseChain(request.Chain, out var chain, out var chainError) || chain is null)
        {
            return OpsResults.Bad(OpsErrorCodes.InvalidChain, chainError ?? "A chain is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Address))
        {
            return OpsResults.Bad(OpsErrorCodes.AddressRequired, "An address is required.");
        }

        var purpose = ScreeningPurpose.PayoutDestination;
        if (!string.IsNullOrWhiteSpace(request.Purpose)
            && !Enum.TryParse(request.Purpose, ignoreCase: true, out purpose))
        {
            return OpsResults.Bad(
                OpsErrorCodes.InvalidPurpose,
                $"Unknown purpose. Expected one of: {string.Join(", ", Enum.GetNames<ScreeningPurpose>())}.");
        }

        var verdict = await screening.ReScreenAsync(
            chain.Value, request.Address.Trim(), purpose, http.RequestAborted);

        // Never a failure response: an unreachable provider is an expected condition, not an exception
        // (§7.1), and it comes back as Unavailable with an evidence row saying so. A 500 here would tell an
        // operator the request broke, when in fact it worked and the answer is "we could not tell".
        return OpsResults.Ok(new
        {
            screeningId = verdict.ScreeningId,
            decision = verdict.Decision.ToString(),
            score = verdict.Score,
            riskLevel = verdict.RiskLevel,
            reasons = verdict.Reasons,
            addressLabel = verdict.AddressLabel,
            reportUrl = verdict.ReportUrl,
            screenedAt = verdict.ScreenedAt,

            // Always false for a re-screen, which bypasses the cache by definition. Emitted anyway so the
            // shape matches the other screening payloads a UI already handles.
            fromCache = verdict.FromCache,
        });
    }

    /// <summary>
    /// The current-verdict list: one row per address, filtered on its latest verdict.
    ///
    /// <para>An unparseable <c>stale</c> value fails model binding and comes back as 400
    /// <c>ops.malformed_request</c> through the host-wide handler.</para>
    /// </summary>
    private static async Task<IResult> CurrentAddressesAsync(
        IAddressScreeningDirectory screenings,
        HttpContext http,
        string? chain = null,
        string? decision = null,
        string? purpose = null,
        bool? stale = null,
        int page = 1,
        int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;

        if (!TryParseChain(chain, out var chainFilter, out var chainError))
        {
            return OpsResults.Bad(OpsErrorCodes.InvalidChain, chainError!);
        }

        if (!TryParseDecision(decision, out var decisionFilter, out var decisionError))
        {
            return decisionError!;
        }

        if (!TryParsePurpose(purpose, out var purposeFilter, out var purposeError))
        {
            return purposeError!;
        }

        var result = await screenings.SearchCurrentAsync(
            new CurrentVerdictFilter(chainFilter, decisionFilter, purposeFilter, stale),
            page, pageSize, http.RequestAborted);

        // Every decision present even at zero, as on the history list, so counters do not come and go.
        var summary = Enum.GetValues<ScreeningDecision>()
            .ToDictionary(d => d.ToString(), d => result.Summary.TryGetValue(d, out var n) ? n : 0);

        return OpsResults.Ok(new
        {
            page,
            pageSize,
            totalCount = result.TotalCount,
            summary,
            items = result.Items.Select(ToRow).ToList(),
        });
    }

    private sealed record LatestRequest(string? Chain, string?[]? Addresses);

    /// <summary>
    /// The current verdict for each of up to 200 addresses on one chain, in one round trip. Stored evidence
    /// only — no provider call, no quota spent.
    /// </summary>
    private static async Task<IResult> LatestForAddressesAsync(
        LatestRequest request, IAddressScreeningDirectory screenings, HttpContext http)
    {
        if (!TryParseChain(request.Chain, out var chain, out var chainError) || chain is null)
        {
            return OpsResults.Bad(OpsErrorCodes.InvalidChain, chainError ?? "A chain is required.");
        }

        // A missing array is refused rather than read as empty: a misspelt field name would otherwise come
        // back as a successful answer about nothing.
        if (request.Addresses is null)
        {
            return OpsResults.Bad(
                OpsErrorCodes.AddressRequired, "An addresses array is required. Send [] to look up nothing.");
        }

        if (request.Addresses.Length > ScreeningLookupLimits.MaxAddresses)
        {
            return OpsResults.Bad(
                OpsErrorCodes.TooManyAddresses,
                $"At most {ScreeningLookupLimits.MaxAddresses} addresses per request; this one sent "
                + $"{request.Addresses.Length}. Split the request.");
        }

        var lookups = await screenings.FindLatestForAddressesAsync(
            chain.Value, [.. request.Addresses.Select(a => a ?? string.Empty)], http.RequestAborted);

        return OpsResults.Ok(new
        {
            items = lookups.Select(l => new
            {
                address = l.Address,

                // Null means never screened, and only that. Unavailable is a verdict, rendered as one.
                screening = l.Screening is null ? null : ToRow(l.Screening),
            }).ToList(),
        });
    }

    private static bool TryParseDecision(string? value, out ScreeningDecision? parsed, out IResult? error)
    {
        parsed = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        // IsDefined as well: Enum.TryParse accepts any integer, so "7" would otherwise parse to a decision
        // that does not exist and quietly match nothing.
        if (Enum.TryParse<ScreeningDecision>(value, ignoreCase: true, out var d) && Enum.IsDefined(d))
        {
            parsed = d;
            return true;
        }

        error = OpsResults.Bad(
            OpsErrorCodes.InvalidDecision,
            $"Unknown decision. Expected one of: {string.Join(", ", Enum.GetNames<ScreeningDecision>())}.");
        return false;
    }

    private static bool TryParsePurpose(string? value, out ScreeningPurpose? parsed, out IResult? error)
    {
        parsed = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (Enum.TryParse<ScreeningPurpose>(value, ignoreCase: true, out var p) && Enum.IsDefined(p))
        {
            parsed = p;
            return true;
        }

        error = OpsResults.Bad(
            OpsErrorCodes.InvalidPurpose,
            $"Unknown purpose. Expected one of: {string.Join(", ", Enum.GetNames<ScreeningPurpose>())}.");
        return false;
    }

    private static object ToRow(ScreeningAdminRow s) => new
    {
        screeningId = s.Id,
        chain = s.Chain.ToString(),
        address = s.Address,
        purpose = s.Purpose.ToString(),
        provider = s.Provider,
        decision = s.Decision.ToString(),
        score = s.Score,
        riskLevel = s.RiskLevel,

        // Includes INDIRECT exposure, which is why a row can list a sanctions indicator and still read
        // Allow. Only a direct designation forces a Block; indirect exposure is evidence a human may want
        // to see, and the vendor has already priced it into the score the thresholds judge.
        reasons = s.Reasons,
        addressLabel = s.AddressLabel,
        reportUrl = s.ReportUrl,
        failureReason = s.FailureReason,
        policy = s.PolicyDescription,
        screenedAt = s.ScreenedAt,
        freshUntil = s.FreshUntil,
    };

    private static bool TryParseChain(string? chain, out Chain? parsed, out string? error)
    {
        parsed = null;
        error = null;

        if (string.IsNullOrWhiteSpace(chain))
        {
            return true;
        }

        if (!Enum.TryParse<Chain>(chain, ignoreCase: true, out var value))
        {
            error = "Unknown chain.";
            return false;
        }

        parsed = value;
        return true;
    }
}
