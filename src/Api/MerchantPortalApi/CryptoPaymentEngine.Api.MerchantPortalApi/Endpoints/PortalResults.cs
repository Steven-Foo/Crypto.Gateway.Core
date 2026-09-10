using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Endpoints;

/// <summary>
/// The single place this host turns a <see cref="Result"/> or a validation failure into an HTTP response
/// (§7.1 — one mapper per host, not one per endpoint file). It replaces thirteen near-identical private
/// <c>Ok</c>/<c>Fail</c>/<c>Bad</c> helpers spread over five endpoint files, which had already begun to
/// diverge — <c>PortalTopUpEndpoints</c> mapped only Conflict and sent everything else to 400, while the
/// others carried the full switch. Mirrors <c>OpsResults</c> on the Back Office host deliberately: two hosts
/// that disagree about their envelope force every shared client to special-case one of them.
///
/// <para><b>Every failure carries a machine-readable <c>errorCode</c></b> alongside the human <c>error</c>
/// string. Without one a portal screen can only branch on HTTP status, which distinguishes 400 from 403 but
/// not "amount exceeds your settled balance" from "that asset is not enabled" — two 400s a merchant would act
/// on completely differently. Prose cannot fill that gap: it is reworded and localised, so code matching on it
/// breaks silently on a copy edit. The code is the contract; the message is for humans.</para>
///
/// <para>Domain failures take their code straight from <see cref="Error.Code"/>, which every module already
/// defines as a stable dotted string (<c>withdrawal.duplicate_reference</c>, <c>merchant.not_found</c>, …), so
/// the reason a merchant's payout was refused reaches the browser unchanged from where it was decided.
/// Host-level input validation uses the <see cref="PortalErrorCodes"/> catalog.</para>
/// </summary>
public static class PortalResults
{
    /// <summary>Success envelope. <c>error</c>/<c>errorCode</c> are present-but-null so the shape is identical
    /// on both paths and a consumer never has to probe for missing fields.</summary>
    public static IResult Ok(object? data) =>
        Results.Ok(new { isSuccess = true, data, error = (string?)null, errorCode = (string?)null });

    /// <summary>
    /// Maps a domain <see cref="Error"/> to its HTTP status by <see cref="ErrorType"/>: NotFound → 404,
    /// Conflict → 409, Unauthorized → 401, everything else → 400.
    ///
    /// <para>A tenant-scoped miss arrives here as NotFound, not Forbidden — the portal never confirms that
    /// another merchant's id is real, so "belongs to someone else" and "does not exist" are indistinguishable
    /// by design.</para>
    /// </summary>
    public static IResult Fail(Error error) =>
        Results.Json(
            new { isSuccess = false, data = (object?)null, error = error.Message, errorCode = error.Code },
            statusCode: StatusFor(error.Type));

    /// <summary>A request this host rejected before it reached a module — an unparseable enum, an unknown
    /// coin, an amount finer than the asset's precision. Takes an explicit stable code.</summary>
    public static IResult Bad(string errorCode, string message) =>
        Results.Json(
            new { isSuccess = false, data = (object?)null, error = message, errorCode },
            statusCode: StatusCodes.Status400BadRequest);

    public static IResult NotFound(string errorCode, string message) =>
        Results.Json(
            new { isSuccess = false, data = (object?)null, error = message, errorCode },
            statusCode: StatusCodes.Status404NotFound);

    public static IResult Unauthorized(string errorCode, string message) =>
        Results.Json(
            new { isSuccess = false, data = (object?)null, error = message, errorCode },
            statusCode: StatusCodes.Status401Unauthorized);

    public static IResult Forbidden(string errorCode, string message) =>
        Results.Json(
            new { isSuccess = false, data = (object?)null, error = message, errorCode },
            statusCode: StatusCodes.Status403Forbidden);

    public static int StatusFor(ErrorType type) => type switch
    {
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        _ => StatusCodes.Status400BadRequest,
    };
}

/// <summary>
/// Stable error codes for failures raised by this <em>host</em> (input validation, auth, CSRF), as opposed to
/// those raised by a module, which carry their own <see cref="Error.Code"/>. Treat these as a published
/// contract: a consumer branches on them, so renaming one is a breaking change.
///
/// <para>Deliberately <c>portal.*</c>, mirroring the Back Office's <c>ops.*</c>. The prefix says which host
/// refused the request, which matters when a merchant reports an error and support has two APIs to search.</para>
/// </summary>
public static class PortalErrorCodes
{
    // ── auth / authorization ──
    public const string Unauthenticated = "portal.unauthenticated";
    public const string InvalidCredentials = "portal.invalid_credentials";
    public const string CsrfInvalid = "portal.csrf_invalid";
    public const string PermissionDenied = "portal.permission_denied";

    // ── request validation ──
    public const string InvalidChain = "portal.invalid_chain";
    public const string InvalidAsset = "portal.invalid_asset";
    public const string InvalidAmount = "portal.invalid_amount";
    public const string InvalidStatus = "portal.invalid_status";
    public const string InvalidPermissionCode = "portal.invalid_permission_code";
    public const string InvalidIpAddress = "portal.invalid_ip_address";
    public const string NetworkRequired = "portal.network_required";
    public const string AddressRequired = "portal.address_required";
    public const string NotFound = "portal.not_found";
}
