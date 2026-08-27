using System.Security.Cryptography;
using System.Text;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;

/// <summary>
/// DEVELOPMENT AND TESTS ONLY. The single source of truth for the dev-tier's deterministic, salt-derived
/// throwaway entropy — shared by <see cref="DevHdWalletProvisioner"/> (which only ever needs the resulting
/// account xpub) and <see cref="DevHdWalletSigningSecretProvider"/> (which needs the same seed one step
/// further, to derive an actual child private key for real dev/testnet signing). Kept as the one place this
/// salt and hash construction exist, so the two can never silently drift apart and derive different keys for
/// the same (merchant id, chain) or (chain) alone.
/// </summary>
internal static class DevHdWalletDeterministicSeed
{
    // Not a production key or a real seed — a dev-only KDF salt that turns a merchant id (or a fixed platform
    // label) into throwaway deterministic entropy. Changing it re-derives all dev addresses. PUBLIC in this
    // repo, so never for real funds.
    private const string DevMasterSalt = "cpe-dev-hdwallet-master-v1-not-for-production";

    public static byte[] ForMerchant(Guid merchantId)
    {
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(DevMasterSalt));
        return hmac.ComputeHash(merchantId.ToByteArray()); // 64 bytes → a valid BIP-32 master seed
    }

    public static byte[] ForPlatformWithdrawal(Chain chain)
    {
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(DevMasterSalt));
        return hmac.ComputeHash(Encoding.UTF8.GetBytes($"platform:withdrawal:{chain}")); // deterministic dev seed, distinct from any merchant's
    }
}
