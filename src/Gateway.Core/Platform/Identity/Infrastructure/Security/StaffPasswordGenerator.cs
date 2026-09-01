using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Security;

/// <summary>Generated temp passwords come from the shared <see cref="TemporaryPassword"/> primitive, so the
/// staff and merchant-portal modules cannot drift on entropy. The port stays module-owned (§4.5).</summary>
public sealed class StaffPasswordGenerator : IStaffPasswordGenerator
{
    public string Generate() => TemporaryPassword.Generate();
}
