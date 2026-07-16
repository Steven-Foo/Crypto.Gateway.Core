using System.Collections.Concurrent;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.MerchantGateway.Endpoints;

/// <summary>
/// DEVELOPMENT ONLY. An in-host sink for the merchant callback the Notification module POSTs when a deposit is
/// detected, so a developer can SEE the callback fire end-to-end without standing up an external receiver.
/// Point the dev merchant's <c>CallbackUrl</c> here (appsettings.Local.json). It stores the most recent
/// callbacks in memory; GET them to review. Never mapped outside Development (§10 — no dev surface in prod).
/// </summary>
public static class DevEndpoints
{
    private static readonly ConcurrentQueue<ReceivedCallback> Received = new();
    private const int MaxRetained = 50;

    private sealed record ReceivedCallback(
        DateTimeOffset ReceivedAt, IReadOnlyDictionary<string, string> Headers, string Body);

    public static void MapDevEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/dev").WithTags("Dev (development only)");

        // Receives + acks the signed merchant callback. Captures the signing headers + body for review.
        group.MapPost("/callbacks", async (HttpContext http) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            var body = await reader.ReadToEndAsync(http.RequestAborted);

            var headers = http.Request.Headers
                .Where(h => h.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

            Received.Enqueue(new ReceivedCallback(DateTimeOffset.UtcNow, headers, body));
            while (Received.Count > MaxRetained && Received.TryDequeue(out _)) { }

            return Results.Ok(new { ok = true }); // 2xx ack so the sender marks it delivered
        });

        // Review the callbacks received so far, newest first.
        group.MapGet("/callbacks", () => Results.Ok(Received.Reverse()));

        // Seeds the deposit scan cursor near the current chain tip. On a fresh DB the scanner would otherwise
        // cold-start at block 1 and crawl from genesis (500 blocks/pass) — never reaching a live mainnet
        // deposit. Call this ONCE after enabling the live TRON adapter and BEFORE sending USDT. Idempotent.
        // (A config-driven cold-start block on the Deposit module is the proper follow-up; this unblocks the
        // human mainnet test now.)
        group.MapPost("/scan-cursor", async (
            string chain, int? lookback, IChainStatusReader status, IScanCursorStore cursors, HttpContext http) =>
        {
            if (!Enum.TryParse<Chain>(chain, ignoreCase: true, out var parsed))
                return Results.BadRequest(new { error = $"Unknown chain '{chain}'." });

            var tip = await status.GetTipHeightAsync(parsed, http.RequestAborted);
            var start = Math.Max(0, tip - Math.Max(0, lookback ?? 20)); // a small lookback so an in-flight tx isn't missed
            await cursors.SetLastScannedBlockAsync(parsed, start, http.RequestAborted);

            return Results.Ok(new { chain = parsed.ToString(), tip, cursorSetTo = start });
        });
    }
}
