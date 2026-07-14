using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Persistence;

public sealed class ScanCursorStore(DepositDbContext context, TimeProvider timeProvider) : IScanCursorStore
{
    public async Task<long> GetLastScannedBlockAsync(Chain chain, CancellationToken cancellationToken = default) =>
        (await context.ScanCursors.AsNoTracking().SingleOrDefaultAsync(c => c.Chain == chain, cancellationToken))
            ?.LastScannedBlock ?? 0L;

    public async Task SetLastScannedBlockAsync(Chain chain, long blockNumber, CancellationToken cancellationToken = default)
    {
        var cursor = await context.ScanCursors.SingleOrDefaultAsync(c => c.Chain == chain, cancellationToken);
        var now = timeProvider.GetUtcNow();

        if (cursor is null)
            context.ScanCursors.Add(new ScanCursor(chain, blockNumber, now));
        else
            cursor.Advance(blockNumber, now);

        await context.SaveChangesAsync(cancellationToken);
    }
}
