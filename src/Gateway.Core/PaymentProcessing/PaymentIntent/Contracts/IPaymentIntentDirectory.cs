namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Contracts;

/// <summary>
/// The public read model behind a hosted pay page. Carries only what the payer needs — no internal id, no
/// merchant data. <see cref="ExpectedAmountBaseUnits"/> is an exact base-unit integer string; the host
/// converts it to a display value at the edge (§14). <see cref="Status"/> is the effective status
/// ("pending" | "confirmed" | "expired"), already accounting for a lapsed-but-not-yet-swept expiry.
/// </summary>
public sealed record PaymentIntentView(
    Guid PublicReference,
    string Address,
    string ExpectedAmountBaseUnits,
    string Status,
    DateTimeOffset ExpiresAt);

public interface IPaymentIntentDirectory
{
    Task<PaymentIntentView?> FindByPublicReferenceAsync(Guid publicReference, CancellationToken cancellationToken = default);
}
