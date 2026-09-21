using CryptoPaymentEngine.Api.OperationsApi.Endpoints;

namespace CryptoPaymentEngine.Api.OperationsApi.Security;

/// <summary>
/// Guarantees that every response from this host carries the standard envelope, including the ones no
/// endpoint ever got to produce.
///
/// <para><b>Why this is not optional.</b> The whole point of REQ-7's <c>errorCode</c> is that a consumer can
/// branch on why something failed instead of pattern-matching prose. That contract held for every failure an
/// endpoint returned, and broke completely for two classes it never saw: a request that fails model binding
/// before the handler runs (a missing required query parameter, a malformed JSON body), and an unhandled
/// exception. Both escaped as a raw framework response — a developer exception page locally, a bodyless 500
/// in production. A front end handling those has to special-case "sometimes there is no envelope", which is
/// exactly the ambiguity the envelope exists to remove.</para>
///
/// <para><b>Registered first</b>, so it wraps authentication, authorization, CORS and routing. An exception
/// thrown inside the auth middleware would otherwise escape the same way.</para>
///
/// <para><b>It never reveals the exception.</b> A stack trace tells an attacker about internals and tells a
/// legitimate user nothing they can act on. The detail goes to the log, where it belongs, with the request
/// path for correlation.</para>
/// </summary>
public sealed class OpsExceptionMiddleware(RequestDelegate next, ILogger<OpsExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException ex)
        {
            // Model binding rejected the request before any handler ran: a required query parameter absent,
            // a body that is not valid JSON, a route value of the wrong shape. This is the caller's mistake,
            // so it is a 400 with a code they can branch on — not a 500, which would send them looking for a
            // server fault that is not there.
            logger.LogInformation(
                "Malformed request to {Method} {Path}: {Reason}",
                context.Request.Method, context.Request.Path, ex.Message);

            await WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                OpsErrorCodes.MalformedRequest,
                ex.Message);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The caller hung up. Nothing to report and nobody to report it to; writing a response here
            // would only throw again on a closed connection.
            logger.LogDebug(
                "Request aborted by the client: {Method} {Path}", context.Request.Method, context.Request.Path);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Unhandled exception in {Method} {Path}", context.Request.Method, context.Request.Path);

            await WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                OpsErrorCodes.InternalError,
                "An unexpected error occurred. The detail has been logged.");
        }
    }

    private static async Task WriteAsync(HttpContext context, int status, string errorCode, string message)
    {
        // If the handler already began writing, the status and headers are gone and anything appended would
        // corrupt a partially-sent body. Better a truncated response than a malformed one.
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";

        await context.Response.WriteAsJsonAsync(new
        {
            isSuccess = false,
            error = message,
            errorCode,
        });
    }
}
