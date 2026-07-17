using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Contracts;
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

        // Simulates a payer sending the invoiced amount, WITHOUT mainnet or real money: scripts the in-memory
        // chain so the real workers do the real work — detection → confirmation → DepositConfirmed → outbox →
        // ledger credit (net + fee split) → invoice match → signed merchant callback. Nothing is faked past
        // the chain boundary; this only stands in for the node (§8's DI seam).
        //
        // Requires the in-memory chain source, i.e. Chains:Tron:Live=false. With Live=true a real node is
        // authoritative and the only way to make a deposit appear is to actually send USDT.
        group.MapPost("/simulate-deposit", async (
            Guid reference,
            decimal? amount,
            IServiceProvider services,
            IPaymentIntentDirectory intents,
            IScanCursorStore cursors,
            IDepositPolicyProvider policies,
            HttpContext http) =>
        {
            if (services.GetService<InMemoryChainSource>() is not { } chain)
                return Results.BadRequest(new { error = "Not using the in-memory chain. Set Chains:Tron:Live=false to simulate, or send real USDT." });

            var intent = await intents.FindByPublicReferenceAsync(reference, http.RequestAborted);
            if (intent is null)
                return Results.NotFound(new { error = $"No payment intent for reference {reference}. Create one via POST /api/v1/deposit first." });

            // Default to the exact invoiced amount so the intent matches; an override lets you exercise an
            // under/over-payment (which still matches — the merchant decides — but flips AmountMatched).
            var baseUnits = amount is null
                ? BigInteger.Parse(intent.ExpectedAmountBaseUnits, CultureInfo.InvariantCulture)
                : new BigInteger(decimal.Truncate(amount.Value * 1_000_000m)); // USDT-TRON: 6 dp

            if (baseUnits <= BigInteger.Zero)
                return Results.BadRequest(new { error = "Amount must be positive." });

            var required = policies.For(Chain.Tron).RequiredConfirmations;
            var tip = await chain.GetTipHeightAsync(Chain.Tron, http.RequestAborted);
            var depositBlock = (tip > 0 ? tip : 1_000) + 1;

            var transfer = new DetectedTransfer(
                Chain.Tron, intent.Address, intent.AssetId, baseUnits,
                $"0xsimulated{depositBlock}", 0, depositBlock, $"h{depositBlock}");

            chain.AddBlock(Chain.Tron, depositBlock, $"h{depositBlock}", transfer);

            // Bury it to the policy depth so the confirmation worker can credit it on its next pass.
            for (var i = 1; i <= required; i++)
                chain.AddBlock(Chain.Tron, depositBlock + i, $"h{depositBlock + i}");

            // Point the scanner just behind the deposit. Without this the cold start would jump the cursor to
            // the tip and skip straight past the block we just wrote.
            await cursors.SetLastScannedBlockAsync(Chain.Tron, depositBlock - 1, http.RequestAborted);

            return Results.Ok(new
            {
                simulated = true,
                reference,
                address = intent.Address,
                amountBaseUnits = baseUnits.ToString(CultureInfo.InvariantCulture),
                depositBlock,
                confirmationsBuried = required,
                whatNext = "Within ~20s the scanner detects it and the confirmation worker credits it. Watch: " +
                           $"GET /pay/{reference}/info flips to 'confirmed', GET /dev/callbacks shows the signed " +
                           "callback, and ledger.Journal/JournalEntry shows the credit + fee split.",
            });
        });
    }
}
