using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;

/// <summary>
/// DEVELOPMENT / LOCAL ONLY. On every host start, re-derives and re-populates the account xpub for every
/// already-existing dev-mode HD wallet (per-merchant deposit wallets + the platform withdrawal pool wallet)
/// into the writable in-memory secret store.
///
/// <see cref="MutableInMemorySecretStore"/> holds nothing across a process restart — deliberately, since a
/// real seed must never touch disk in this tier (§10). <see cref="DevHdWalletProvisioner"/> only ever writes a
/// wallet's xpub into that store at the moment the wallet is FIRST minted; <c>WalletDerivationService</c> never
/// re-provisions an already-existing row, it only allocates the next child index from it. So without this
/// step, any merchant (or the withdrawal pool) whose wallet was created before the most recent restart could
/// never derive another address — exactly the gap this closes.
///
/// This is safe because the xpub is a pure, deterministic function of (merchant id, chain) — or (chain) alone
/// for the platform pool (see <see cref="DevHdWalletProvisioner.ResolveMerchantDepositXpub"/>/
/// <see cref="DevHdWalletProvisioner.ResolvePlatformWithdrawalXpub"/>). Recomputing it here always reproduces
/// the exact same account xpub — and therefore the exact same address tree — as the one originally derived.
/// No wallet row, derived address, balance, or invoice is read or written by this step; it only repopulates
/// the in-memory store.
/// </summary>
public sealed class DevHdWalletReseeder(
    IServiceScopeFactory scopeFactory,
    MutableInMemorySecretStore secrets,
    DevHdWalletProvisioner provisioner,
    ILogger<DevHdWalletReseeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IHdWalletRepository>();

        IReadOnlyList<DevReseedCandidate> wallets;
        try
        {
            wallets = await repository.ListActiveDevelopmentWalletsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // DEV convenience must never brick host startup (same posture as DevHdWalletSeeder). Existing
            // wallets simply stay unable to allocate a new address until the next successful restart.
            logger.LogWarning(ex,
                "Dev HD-wallet re-seed skipped; existing merchants/the withdrawal pool will fail to allocate a "
                + "new address until this is resolved (is the KeyManagement schema migrated on this database?).");
            return;
        }

        var reseeded = 0;
        foreach (var wallet in wallets)
        {
            try
            {
                var xpub = wallet.MerchantId is { } merchantId
                    ? provisioner.ResolveMerchantDepositXpub(merchantId, wallet.Chain)
                    : provisioner.ResolvePlatformWithdrawalXpub(wallet.Chain);

                secrets.Put(wallet.PublicKeyReference, xpub);
                reseeded++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to re-seed dev secret for HD wallet {WalletId} ({Chain}); it will fail to allocate "
                    + "a new address until this is resolved.", wallet.Id, wallet.Chain);
            }
        }

        if (reseeded > 0)
            logger.LogInformation(
                "Re-seeded {Count} existing dev HD wallet xpub(s) into the in-memory secret store.", reseeded);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
