using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application.Abstractions;

/// <summary>Persistence port for the append-only policy history.</summary>
public interface IScreeningPolicyRepository
{
    /// <summary>The version currently in force, or null when nobody has ever set one — in which case the
    /// configured defaults apply.</summary>
    Task<ScreeningPolicyVersion?> FindLatestAsync(CancellationToken cancellationToken = default);

    /// <summary>Every version, newest first. The audit trail of who calibrated the control and when.</summary>
    Task<IReadOnlyList<ScreeningPolicyVersion>> ListAsync(
        int limit, CancellationToken cancellationToken = default);

    /// <summary>Append a version. Never an update — the aggregate has no mutators.</summary>
    Task AddAsync(ScreeningPolicyVersion version, CancellationToken cancellationToken = default);
}
