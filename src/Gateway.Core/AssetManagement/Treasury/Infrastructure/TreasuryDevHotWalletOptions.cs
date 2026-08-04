namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure;

/// <summary>
/// DEV/TESTNET-tier config for seeding the platform hot withdrawal wallet(s). Bound from
/// <c>Treasury:DevHotWallets</c>. <see cref="SecretReference"/> points at the private key registered in the
/// in-memory secret store (<c>KeyManagement:DevSecrets</c>) — this config holds no key material (§10), and
/// the reference must be colon-free (a colon in a <c>DevSecrets</c> dictionary key is silently truncated by
/// .NET config's <c>GetChildren()</c>).
/// </summary>
public sealed class TreasuryDevHotWalletOptions
{
    public const string SectionName = "Treasury";

    public List<DevHotWalletSeed> DevHotWallets { get; init; } = [];
}

public sealed class DevHotWalletSeed
{
    public string Chain { get; init; } = null!;
    public string Address { get; init; } = null!;
    public string SecretReference { get; init; } = null!;
    public string? Description { get; init; }
}
