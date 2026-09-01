using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure.Persistence;

/// <summary>
/// Tells the deposit scanner whether the invoice holding an address is a customer payment or a merchant
/// top-up, so the deposit can be priced with the right fee the moment it is detected.
///
/// <para>A single-row read against the same filtered unique index (<c>UX_PaymentIntent_LiveWallet</c>) that
/// guarantees at most one <c>Waiting</c> invoice per address — so this can never have to pick between two
/// candidates. No waiting invoice ⇒ null ⇒ the caller treats it as a customer deposit, which is the correct
/// reading of a direct or late transfer and the safe default (it withholds the funds for the settlement
/// period rather than releasing them early).</para>
/// </summary>
public sealed class DepositKindResolver(PaymentIntentDbContext context) : IDepositKindResolver
{
    public async Task<string?> FindWaitingKindAsync(Guid walletId, CancellationToken cancellationToken = default)
    {
        var kind = await context.PaymentIntents.AsNoTracking()
            .Where(i => i.WalletId == walletId && i.Status == PaymentIntentStatus.Waiting)
            .Select(i => (PaymentIntentKind?)i.Kind)
            .FirstOrDefaultAsync(cancellationToken);

        return kind?.ToString();
    }
}
