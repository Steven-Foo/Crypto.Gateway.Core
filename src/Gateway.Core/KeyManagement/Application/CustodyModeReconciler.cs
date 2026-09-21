using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Application;

/// <summary>What one custody-mode reconciliation changed.</summary>
public sealed record CustodySwitchOutcome(int Archived, int Reactivated);

/// <summary>
/// Keeps HD-wallet rows consistent with the secret store a TESTNET host runs with, so staging can switch between
/// the in-memory store and AWS KMS and back.
///
/// <para>Why it is needed: every wallet row records the store that holds its secret, a host registers exactly one
/// store (§10), and the filtered unique index allows one ACTIVE wallet per (merchant, chain, purpose). A wallet whose
/// store is switched off can neither derive nor sign, yet it would still block its replacement from being created.</para>
///
/// <para>The rule, in one transaction: (1) archive every active wallet of the other store; (2) for each
/// (merchant, chain, purpose) left with no active wallet, reactivate the most recently archived wallet of the store
/// in force, if there is one. Otherwise the provisioner mints a new wallet on first use, exactly as on a fresh
/// database. Disabled wallets are never touched. A reactivated wallet keeps its derivation index, so it resumes after
/// the last address it issued and never hands one out twice.</para>
///
/// <para>Archiving is safe for funds already on-chain. Detection needs no key, so an archived wallet's addresses keep
/// receiving and crediting deposits; they cannot be swept or paid out from until the store holding their key is back.
/// A withdrawal pool with no signable wallet parks payouts as AwaitingFunds with the reserve held.</para>
///
/// <para>Idempotent: run again in the same mode it changes nothing. Production never calls it: production custody is
/// KMS from the first wallet.</para>
/// </summary>
public sealed class CustodyModeReconciler(IHdWalletRepository repository, TimeProvider timeProvider)
{
    public Task<Result<CustodySwitchOutcome>> ReconcileAsync(
        SecretProviderKind activeKind, CancellationToken cancellationToken = default) =>
        repository.InTransactionAsync(async ct =>
        {
            var wallets = await repository.ListForCustodySwitchAsync(ct);
            var now = timeProvider.GetUtcNow();

            // Archive, and save, before reactivating anything. SQL Server checks the filtered unique index per
            // statement, so a reactivation written ahead of its group's archive would be refused.
            var archived = 0;
            foreach (var wallet in wallets.Where(w => w.IsActive && w.SecretProvider != activeKind))
            {
                wallet.Archive(now);
                archived++;
            }

            if (archived > 0)
                await repository.SaveChangesAsync(ct);

            var reactivated = 0;
            foreach (var group in wallets.GroupBy(w => (w.MerchantId, w.Chain, w.Purpose)))
            {
                if (group.Any(w => w.IsActive))
                    continue;

                var restore = group
                    .Where(w => w.Status == HdWalletStatus.Archived && w.SecretProvider == activeKind)
                    .OrderByDescending(w => w.UpdatedAt)
                    .ThenByDescending(w => w.CreatedAt)
                    .FirstOrDefault();
                if (restore is null)
                    continue;

                var result = restore.Reactivate(now);
                if (result.IsFailure)
                    return Result.Failure<CustodySwitchOutcome>(result.Error!);

                reactivated++;
            }

            if (reactivated > 0)
                await repository.SaveChangesAsync(ct);

            return Result.Success(new CustodySwitchOutcome(archived, reactivated));
        }, cancellationToken);
}
