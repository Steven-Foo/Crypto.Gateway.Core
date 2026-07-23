using System.Globalization;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence;

public sealed class WithdrawalDirectory(WithdrawalDbContext context) : IWithdrawalDirectory
{
    public async Task<WithdrawalView?> FindByMerchantReferenceAsync(
        Guid merchantId, string merchantTransactionId, CancellationToken cancellationToken = default)
    {
        var withdrawal = await context.Withdrawals.AsNoTracking()
            .SingleOrDefaultAsync(
                w => w.MerchantId == merchantId && w.IdempotencyKey == merchantTransactionId, cancellationToken);

        if (withdrawal is null)
            return null;

        return new WithdrawalView(
            withdrawal.Id,
            withdrawal.AssetId,
            withdrawal.Chain,
            withdrawal.DestinationAddress,
            withdrawal.Amount.ToString(CultureInfo.InvariantCulture),
            withdrawal.Fee.ToString(CultureInfo.InvariantCulture),
            withdrawal.Status.ToString(),
            withdrawal.TransactionHash,
            withdrawal.CreatedAt);
    }
}
