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
}
