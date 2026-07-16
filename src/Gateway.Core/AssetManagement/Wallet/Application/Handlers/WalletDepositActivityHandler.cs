using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Events;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application.Handlers;

/// <summary>
/// Bumps a wallet's deposit-activity counter on every confirmed deposit — the non-money-duplicating proxy
/// this module uses instead of a balance column (see <c>Wallet.DepositsReceivedCount</c>). Idempotent by
/// construction is not required here the way it is for the Ledger: double-counting a redelivered event
/// only ever biases which idle wallet gets reused next, it can never move money, so this handler stays
/// simple rather than needing its own dedup key.
/// </summary>
public sealed class WalletDepositActivityHandler(IWalletRepository repository)
    : IIntegrationEventHandler<DepositConfirmed>
{
    public async Task HandleAsync(DepositConfirmed @event, CancellationToken cancellationToken = default)
    {
        var wallet = await repository.GetByIdAsync(@event.WalletId, cancellationToken);
        if (wallet is null)
            return; // the wallet always exists for a real confirmed deposit; nothing to do if not (e.g. test data)

        wallet.RecordDepositReceived(@event.ConfirmedAt);
        await repository.SaveChangesAsync(cancellationToken);
    }
}
