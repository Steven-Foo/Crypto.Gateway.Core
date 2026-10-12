using CryptoPaymentEngine.Api.MerchantPortalApi.Endpoints;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Security;

/// <summary>
/// Guarantees every response from the merchant portal carries the standard envelope (<c>isSuccess</c>,
/// <c>error</c>, <c>errorCode</c>) — including the ones no endpoint ever got to produce. The portal twin of the
/// Ops host's <c>OpsExceptionMiddleware</c>.
///
/// <para><b>Found the hard way (2026-10-12):</b> the merchant frontend sent <c>{"roleId":""}</c>, model binding
/// rejected it before any handler ran, and the response was a raw developer error page — a full stack trace —
/// instead of an envelope the frontend could branch on. In production it would have been a bare 400 with no
/// <c>errorCode</c> at all.</para>
///
/// <para><b>Registered first</b>, so it wraps CORS, the session middleware and routing. <b>It never reveals the
/// exception</b>: the detail goes to the log with the request path for correlation.</para>
/// </summary>
public sealed class PortalExceptionMiddleware(RequestDelegate next, ILogger<PortalExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException ex)
        {
            // Rejected before any handler ran: a body that isn't valid JSON for its type (e.g. "" for an id), a
            // missing required query parameter, a route value of the wrong shape. The caller's mistake → 400.
            logger.LogInformation(
                "Malformed request to {Method} {Path}: {Reason}",
                context.Request.Method, context.Request.Path, ex.Message);

            await WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                PortalErrorCodes.MalformedRequest,
                ex.InnerException is System.Text.Json.JsonException
                    ? "The request body is not valid for this endpoint. Check the field types (for example, send an id or null, never an empty string)."
                    : ex.Message);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The caller hung up; writing a response would only throw on a closed connection.
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
                PortalErrorCodes.InternalError,
                "An unexpected error occurred. The detail has been logged.");
        }
    }

    private static async Task WriteAsync(HttpContext context, int status, string errorCode, string message)
    {
        // If the handler already began writing, appending would corrupt a partially-sent body.
        if (context.Response.HasStarted)
            return;

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
