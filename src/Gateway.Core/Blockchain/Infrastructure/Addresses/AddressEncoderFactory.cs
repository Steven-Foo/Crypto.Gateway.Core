using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;

/// <summary>
/// Capability segregation (§8): an unsupported chain is simply absent, rather than an adapter that
/// throws <c>NotSupportedException</c> from a method it was forced to implement.
/// </summary>
public sealed class AddressEncoderFactory : IAddressEncoderFactory
{
    private readonly Dictionary<Chain, IAddressEncoder> _encoders;

    public AddressEncoderFactory(IEnumerable<IAddressEncoder> encoders) =>
        _encoders = encoders.ToDictionary(e => e.Chain);

    public bool Supports(Chain chain) => _encoders.ContainsKey(chain);

    public IAddressEncoder For(Chain chain) =>
        _encoders.TryGetValue(chain, out var encoder)
            ? encoder
            : throw new InvalidOperationException($"No address encoder is registered for {chain}.");
}
