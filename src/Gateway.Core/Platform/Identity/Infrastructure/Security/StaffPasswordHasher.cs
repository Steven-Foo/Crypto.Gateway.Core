using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Security;

/// <summary>
/// Staff passwords, hashed with the shared PBKDF2-SHA256 primitive (<see cref="Pbkdf2PasswordHash"/>) so the
/// scheme stays identical to every other identity module instead of drifting as a copy. The port stays
/// module-owned (§4.5) — only the algorithm is shared, never the session/identity model.
/// </summary>
public sealed class StaffPasswordHasher : IStaffPasswordHasher
{
    public string Hash(string password) => Pbkdf2PasswordHash.Hash(password);

    public bool Verify(string password, string hash) => Pbkdf2PasswordHash.Verify(password, hash);
}
