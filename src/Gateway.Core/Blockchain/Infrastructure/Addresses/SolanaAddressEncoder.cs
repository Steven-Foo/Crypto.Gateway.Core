using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;

/// <summary>
/// A Solana address <em>is</em> the ed25519 public key, rendered as plain Base58 — no prefix and,
/// unlike TRON, no checksum.
/// </summary>
public sealed class SolanaAddressEncoder : IAddressEncoder
{
    public const int PublicKeyLength = 32;

    public Chain Chain => Chain.Solana;

    public string Encode(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != PublicKeyLength)
        {
            throw new ArgumentException(
                $"Expected a {PublicKeyLength}-byte ed25519 public key, got {publicKey.Length} bytes.",
                nameof(publicKey));
        }

        return Base58.Encode(publicKey);
    }
}
