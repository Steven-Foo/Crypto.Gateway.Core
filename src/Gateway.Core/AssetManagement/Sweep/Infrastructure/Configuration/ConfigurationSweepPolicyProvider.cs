using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Configuration;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Infrastructure.Configuration;

/// <summary>
/// Reads each chain's deployed sweep defaults from <c>Sweep:Policies</c>. All amounts are exact base-unit
/// integers (never display values — §14).
///
/// <para>These are the <b>defaults</b>, not the effective policy: a chain's stored settings row starts from
/// them and keeps tracking them until staff save their own values (see <c>SweepSettings</c>). A chain absent
/// from configuration has no settings row and is never swept, which is what lets an ops screen report "not
/// configured" instead of inventing a threshold.</para>
/// </summary>
public sealed class ConfigurationSweepDefaults : ISweepConfigurationDefaults
{
    /// <summary>Two minutes — the cadence sweeps ran at before the interval became configurable, so an
    /// environment that says nothing keeps behaving exactly as it did.</summary>
    public const int DefaultScanIntervalMinutes = 2;

    private readonly IReadOnlyDictionary<Chain, SweepConfigurationDefault> _defaults;

    public ConfigurationSweepDefaults(IConfiguration configuration)
    {
        var defaults = new Dictionary<Chain, SweepConfigurationDefault>();

        foreach (var child in configuration.GetSection("Sweep:Policies").GetChildren())
        {
            if (!Enum.TryParse<Chain>(child.Key, ignoreCase: true, out var chain))
                continue;

            var minSweep = BigInteger.Parse(child["MinSweepAmountBaseUnits"] ?? "0", CultureInfo.InvariantCulture);
            var confirmations = int.Parse(child["Confirmations"] ?? "0", CultureInfo.InvariantCulture);
            var interval = int.Parse(
                child["ScanIntervalMinutes"] ?? DefaultScanIntervalMinutes.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture);

            defaults[chain] = new SweepConfigurationDefault(minSweep, confirmations, interval);
        }

        _defaults = defaults;
    }

    public IReadOnlyDictionary<Chain, SweepConfigurationDefault> Configured => _defaults;
}
