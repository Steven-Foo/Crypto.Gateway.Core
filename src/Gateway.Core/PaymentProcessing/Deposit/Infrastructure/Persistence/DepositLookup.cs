using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Persistence;

public sealed class DepositLookup(DepositDbContext context) : IDepositLookup
{
    public Task<bool> HasDetectedDepositAsync(Chain chain, string address, CancellationToken cancellationToken = default) =>
        context.Deposits.AsNoTracking().AnyAsync(
            d => d.Chain == chain && d.Address == address && d.Status == DepositStatus.Detected,
            cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, DepositSummaryView>> GetByIdsAsync(
        IReadOnlyCollection<Guid> depositIds, CancellationToken cancellationToken = default)
    {
        if (depositIds.Count == 0)
            return new Dictionary<Guid, DepositSummaryView>();

        var deposits = await context.Deposits.AsNoTracking()
            .Where(d => depositIds.Contains(d.Id))
            .ToListAsync(cancellationToken);

        return deposits.ToDictionary(
            d => d.Id,
            d => new DepositSummaryView(
                d.Id,
                d.Amount.ToString(CultureInfo.InvariantCulture),
                d.Fee.ToString(CultureInfo.InvariantCulture),
                d.Confirmations,
                d.TransactionHash));
    }

    public async Task<DepositAmountTotals> SumByIdsAsync(
        IReadOnlyCollection<Guid> depositIds, CancellationToken cancellationToken = default)
    {
        if (depositIds.Count == 0)
            return new DepositAmountTotals("0", "0");

        // Narrow projection, folded client-side — see PaymentIntentDirectory.GetTotalsAsync for why (no SQL
        // Sum() translation precedent for this project's BigInteger money mapping, §14).
        var rows = await context.Deposits.AsNoTracking()
            .Where(d => depositIds.Contains(d.Id))
            .Select(d => new { d.Amount, d.Fee })
            .ToListAsync(cancellationToken);

        var totalAmount = rows.Aggregate(BigInteger.Zero, (sum, r) => sum + r.Amount);
        var totalFee = rows.Aggregate(BigInteger.Zero, (sum, r) => sum + r.Fee);

        return new DepositAmountTotals(
            totalAmount.ToString(CultureInfo.InvariantCulture), totalFee.ToString(CultureInfo.InvariantCulture));
    }
}
