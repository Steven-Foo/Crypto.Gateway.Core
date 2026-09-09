using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Configuration;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure.Configuration;

/// <summary>Reads the same <c>Deposit:Policies</c> config section the Deposit module binds its dust floor
/// from — see <see cref="IPlatformDepositFloor"/> for why this is its own reader rather than a cross-module
/// call. A chain with no configured policy floors at zero (no minimum), matching "unconfigured ⇒ unbounded"
/// rather than silently blocking every invoice for that chain.</summary>
public sealed class ConfigurationPlatformDepositFloor : IPlatformDepositFloor
{
    private readonly IReadOnlyDictionary<Chain, BigInteger> _floors;

    public ConfigurationPlatformDepositFloor(IConfiguration configuration)
    {
        var floors = new Dictionary<Chain, BigInteger>();

        foreach (var child in configuration.GetSection("Deposit:Policies").GetChildren())
        {
            if (!Enum.TryParse<Chain>(child.Key, ignoreCase: true, out var chain))
                continue;

            floors[chain] = BigInteger.Parse(child["MinDepositBaseUnits"] ?? "0", CultureInfo.InvariantCulture);
        }

        _floors = floors;
    }

    public BigInteger For(Chain chain) => _floors.TryGetValue(chain, out var floor) ? floor : BigInteger.Zero;
}
