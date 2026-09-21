using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;

/// <summary>The secret store a testnet host was composed with (see <c>AddTestnetKeyCustody</c>).</summary>
public sealed record CustodyModeOptions(SecretProviderKind ActiveKind);

/// <summary>
/// DEV/TESTNET tier, money host only. On boot, brings HD-wallet rows in line with the secret store this process was
/// composed with (<see cref="CustodyModeReconciler"/>).
///
/// <para>Unlike the dev seeders, a failure here stops the host. A host whose active wallets belong to a store it cannot
/// reach would start and then refuse every deposit address and signature, which is harder to diagnose than a host
/// that does not start and says why.</para>
/// </summary>
public sealed class CustodyModeReconciliationService(
    IServiceScopeFactory scopeFactory,
    CustodyModeOptions options,
    ILogger<CustodyModeReconciliationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var reconciler = scope.ServiceProvider.GetRequiredService<CustodyModeReconciler>();

        var result = await reconciler.ReconcileAsync(options.ActiveKind, cancellationToken);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Custody-mode switch to {options.ActiveKind} failed: {result.Error!.Code} {result.Error.Message}");
        }

        var outcome = result.Value;
        if (outcome.Archived > 0 || outcome.Reactivated > 0)
        {
            logger.LogWarning(
                "Custody mode {Mode}: archived {Archived} HD wallet(s) of the other secret store and reactivated {Reactivated}. "
                + "Addresses of an archived wallet still receive and credit deposits, but cannot be swept or paid out from "
                + "until the store holding their key is switched back.",
                options.ActiveKind, outcome.Archived, outcome.Reactivated);
        }
        else
        {
            logger.LogInformation("Custody mode {Mode}: HD wallets already consistent.", options.ActiveKind);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
