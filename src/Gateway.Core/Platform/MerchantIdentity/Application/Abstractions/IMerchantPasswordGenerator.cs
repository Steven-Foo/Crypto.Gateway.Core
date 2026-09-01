namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;

/// <summary>Generates the one-time password handed over when an admin creates an account or resets one. A
/// module-owned port (§4.5) over the shared <c>TemporaryPassword</c> primitive — an admin never chooses
/// another user's password.</summary>
public interface IMerchantPasswordGenerator
{
    string Generate();
}
