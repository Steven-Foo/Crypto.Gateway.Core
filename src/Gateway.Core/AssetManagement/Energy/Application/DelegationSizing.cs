using System.Numerics;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;

/// <summary>
/// How much staked TRX to lend an address so it has the energy one transfer needs.
///
/// <para>TRON shares a fixed daily energy supply (<c>TotalEnergyLimit</c>) between everyone who stakes, in
/// proportion to the TRX they stake (<c>TotalEnergyWeight</c>, whole TRX). So energy per staked TRX is
/// <c>limit / weight</c>, and the TRX that yields <c>shortfall</c> energy is
/// <c>shortfall × weight / limit</c>. On mainnet that is roughly 10 energy per TRX, so a 131,000-energy transfer
/// needs about 13,000 TRX lent; on a test network the rate is very different, which is why it is read, not fixed.</para>
///
/// <para>All integer arithmetic, rounded UP at every step, so the address is never left short by a rounding
/// remainder (§14). A margin is added for rate drift; the result is at least 1 TRX (the network minimum) and at
/// most <see cref="EnergyOperationOptions.MaxDelegateTrxSun"/>. When the rate is unknown the configured fallback
/// <see cref="EnergyOperationOptions.DelegateTrxSun"/> is used.</para>
/// </summary>
public static class DelegationSizing
{
    private static readonly BigInteger SunPerTrx = 1_000_000;

    public static BigInteger SunToLend(
        BigInteger energyShortfall, BigInteger networkEnergyLimit, BigInteger networkEnergyWeight, EnergyOperationOptions options)
    {
        if (networkEnergyLimit <= 0 || networkEnergyWeight <= 0)
            return options.DelegateTrxSun;

        if (energyShortfall <= 0)
            energyShortfall = BigInteger.One;

        var trx = CeilDiv(energyShortfall * networkEnergyWeight, networkEnergyLimit);
        trx = CeilDiv(trx * (100 + Math.Max(0, options.DelegateMarginPercent)), 100);
        if (trx < 1)
            trx = 1;

        return BigInteger.Min(trx * SunPerTrx, options.MaxDelegateTrxSun);
    }

    private static BigInteger CeilDiv(BigInteger numerator, BigInteger denominator) =>
        (numerator + denominator - 1) / denominator;
}
