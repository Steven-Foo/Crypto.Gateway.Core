namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;

/// <summary>
/// Generates the one-time initial/reset password for a staff account — the admin never types a password
/// in, matching how merchant credentials are generated rather than chosen (§10-adjacent: never trust a
/// human-chosen password for an account with Ops access). The raw value is returned to the caller exactly
/// once; only its hash (via <see cref="IStaffPasswordHasher"/>) is ever persisted.
/// </summary>
public interface IStaffPasswordGenerator
{
    string Generate();
}
