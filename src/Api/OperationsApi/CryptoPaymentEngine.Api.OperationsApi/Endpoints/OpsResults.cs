using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// The single place this host turns a <see cref="Result"/> or a validation failure into an HTTP response
/// (§7.1 — one mapper per host, not one per endpoint file). Before this existed there were eight near-identical
/// private <c>Fail(Error)</c> helpers that had already drifted apart on status codes, which is exactly the
/// failure mode a central mapper prevents.
///
/// <para><b>Every failure carries a machine-readable <c>errorCode</c></b> alongside the human <c>error</c>
/// string. A consumer must be able to branch on <em>why</em> something failed without pattern-matching on
/// display prose — prose is localised, reworded, and reformatted, so matching on it produces UI that breaks
/// silently on a copy edit. The code is the contract; the message is for humans.</para>
///
/// <para>Domain failures get their code straight from <see cref="Error.Code"/>, which every module already
/// defines as a stable dotted string (<c>wallet.not_found</c>, <c>withdrawal.duplicate_reference</c>, …).
/// Host-level input validation uses the <see cref="OpsErrorCodes"/> catalog.</para>
/// </summary>
public static class OpsResults
{
    /// <summary>Success envelope. <c>error</c>/<c>errorCode</c> are present-but-null so the envelope shape is
    /// identical on both paths and a consumer never has to probe for missing fields.</summary>
    public static IResult Ok(object? data) =>
        Results.Ok(new { isSuccess = true, data, error = (string?)null, errorCode = (string?)null });

    /// <summary>
    /// Maps a domain <see cref="Error"/> to its HTTP status by <see cref="ErrorType"/>. This is the canonical
    /// mapping for the whole host: NotFound → 404, Conflict → 409, Unauthorized → 401, everything else → 400.
    /// </summary>
    public static IResult Fail(Error error) =>
        Results.Json(
            new { isSuccess = false, error = error.Message, errorCode = error.Code },
            statusCode: StatusFor(error.Type));

    /// <summary>A request the host itself rejected before reaching a module — an unparseable enum, a missing
    /// companion parameter, an amount past the asset's precision. Takes an explicit stable code.</summary>
    public static IResult Bad(string errorCode, string message) =>
        Results.Json(new { isSuccess = false, error = message, errorCode }, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>A resource the caller may address but that does not exist (or that they may not see — a
    /// tenant-scoped miss is reported as "not found", never as "forbidden", so the response never confirms
    /// that someone else's id is real).</summary>
    public static IResult NotFound(string errorCode, string message) =>
        Results.Json(new { isSuccess = false, error = message, errorCode }, statusCode: StatusCodes.Status404NotFound);

    public static IResult Unauthorized(string errorCode, string message) =>
        Results.Json(new { isSuccess = false, error = message, errorCode }, statusCode: StatusCodes.Status401Unauthorized);

    public static IResult Forbidden(string errorCode, string message) =>
        Results.Json(new { isSuccess = false, error = message, errorCode }, statusCode: StatusCodes.Status403Forbidden);

    public static int StatusFor(ErrorType type) => type switch
    {
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        _ => StatusCodes.Status400BadRequest,
    };
}

/// <summary>
/// The one place this host converts a rate between the wire format and the domain's basis points.
/// <b>Standardization rule (§ frontend integration doc "Percent vs basis points"): every percent-shaped field
/// crossing this host's HTTP boundary — request or response — is a plain percent (<c>2</c> = 2%), never
/// basis points.</b> Only the domain (<c>FeeSchedule</c>, <c>MerchantAssetPolicy</c>, ...) speaks bps; nothing
/// upstream of this conversion should.
/// </summary>
public static class OpsPercent
{
    /// <summary>Plain percent (e.g. <c>2</c> = 2%) → basis points, requiring an EXACT conversion (at most 2
    /// decimal places on the input, 0-100% inclusive) — rejected, never rounded, same "never truncate money"
    /// rule as an amount (§14).</summary>
    public static bool TryToBps(decimal percent, out int bps)
    {
        bps = 0;
        if (percent < 0m)
            return false;

        var scaled = percent * 100m;
        if (scaled != decimal.Truncate(scaled) || scaled > 10_000m)
            return false;

        bps = (int)scaled;
        return true;
    }

    /// <summary>Basis points → plain percent, for a read-back response.</summary>
    public static decimal ToPercent(int bps) => bps / 100m;
}

/// <summary>
/// Stable error codes for failures raised by this <em>host</em> (input validation and auth), as opposed to
/// those raised by a module, which carry their own <see cref="Error.Code"/>. Treat these as a published
/// contract: a consumer branches on them, so rename one only as a breaking change.
/// </summary>
public static class OpsErrorCodes
{
    // ── auth / authorization ──
    public const string Unauthenticated = "ops.unauthenticated";
    public const string CsrfInvalid = "ops.csrf_invalid";
    public const string PermissionDenied = "ops.permission_denied";
    public const string InvalidCredentials = "ops.invalid_credentials";

    // ── request validation ──
    public const string InvalidChain = "ops.invalid_chain";
    public const string InvalidStatus = "ops.invalid_status";
    public const string InvalidWalletType = "ops.invalid_wallet_type";
    public const string InvalidWithdrawalKind = "ops.invalid_withdrawal_kind";
    public const string InvalidCallbackType = "ops.invalid_callback_type";
    public const string InvalidKind = "ops.invalid_kind";
    public const string InvalidHex = "ops.invalid_hex";
    public const string InvalidAmount = "ops.invalid_amount";
    public const string InvalidAsset = "ops.invalid_asset";
    public const string InvalidIpAddress = "ops.invalid_ip_address";
    public const string NetworkRequired = "ops.network_required";
    public const string MerchantIdRequired = "ops.merchant_id_required";
    public const string NotFound = "ops.not_found";
}
