using System.Globalization;
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
            d => new DepositSummaryView(d.Id, d.Amount.ToString(CultureInfo.InvariantCulture), d.Confirmations));
    }
}
