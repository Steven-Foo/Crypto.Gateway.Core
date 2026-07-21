using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

/// <summary>
/// A staff/operator account for the Ops-facing surface — distinct from <c>Merchant</c> (an external
/// partner) and from any future merchant-portal login. Deliberately minimal: no suspend/activate yet
/// (not asked for; add a <c>Status</c> when there's a real caller for it), no per-permission matrix,
/// just a username, a password hash, and a flat <see cref="StaffRole"/>.
/// </summary>
public sealed class StaffUser : Entity<Guid>
{
    private StaffUser(Guid id, string username, string passwordHash, StaffRole role, DateTimeOffset now) : base(id)
    {
        Username = username;
        PasswordHash = passwordHash;
        Role = role;
        CreatedAt = now;
    }

    private StaffUser() : base(Guid.Empty)
    {
    }

    public string Username { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public StaffRole Role { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Result<StaffUser> Create(string username, string passwordHash, StaffRole role, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(username))
            return Result.Failure<StaffUser>(StaffUserErrors.UsernameRequired);

        if (string.IsNullOrWhiteSpace(passwordHash))
            return Result.Failure<StaffUser>(StaffUserErrors.PasswordHashRequired);

        return Result.Success(new StaffUser(Guid.CreateVersion7(), username.Trim(), passwordHash, role, now));
    }
}
