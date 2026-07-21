using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;

public sealed record LoginCommand(string Username, string Password);

public sealed record LoginResult(string Token, DateTimeOffset ExpiresAt, StaffRole Role);

/// <summary>A validated bearer session — what the host middleware needs to authorize a request.</summary>
public sealed record StaffPrincipal(Guid StaffUserId, StaffRole Role);

public interface IStaffAuthService
{
    Task<Result<LoginResult>> LoginAsync(LoginCommand command, CancellationToken cancellationToken = default);

    /// <summary>Revokes the session behind this raw token. Idempotent — logging out twice is not an error.</summary>
    Task<Result> LogoutAsync(string rawToken, CancellationToken cancellationToken = default);
}

public interface IStaffSessionValidator
{
    Task<Result<StaffPrincipal>> ValidateAsync(string rawToken, CancellationToken cancellationToken = default);
}

/// <summary>
/// Login/logout/session-validation in one class — they share the same small dependency set and none of
/// them is complex enough alone to earn its own file the way Create/Fail did for PaymentIntent.
/// </summary>
public sealed class StaffAuthService(
    IStaffUserRepository userRepository,
    IStaffSessionRepository sessionRepository,
    IStaffPasswordHasher passwordHasher,
    IBearerTokenGenerator tokenGenerator,
    IOptions<StaffAuthOptions> options,
    TimeProvider timeProvider) : IStaffAuthService, IStaffSessionValidator
{
    private readonly StaffAuthOptions _options = options.Value;

    public async Task<Result<LoginResult>> LoginAsync(LoginCommand command, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByUsernameAsync(command.Username.Trim(), cancellationToken);

        // Same error for "no such user" and "wrong password" — don't let the response shape confirm
        // whether a username exists.
        if (user is null || !passwordHasher.Verify(command.Password, user.PasswordHash))
            return Result.Failure<LoginResult>(StaffUserErrors.InvalidCredentials);

        var now = timeProvider.GetUtcNow();
        var token = tokenGenerator.Generate();
        var session = StaffSession.Issue(user.Id, token.Hash, user.Role, TimeSpan.FromHours(_options.SessionTtlHours), now);

        sessionRepository.Add(session);
        await sessionRepository.SaveChangesAsync(cancellationToken);

        return Result.Success(new LoginResult(token.RawToken, session.ExpiresAt, user.Role));
    }

    public async Task<Result> LogoutAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        var session = await sessionRepository.FindByTokenHashAsync(tokenGenerator.HashOf(rawToken), cancellationToken);
        if (session is null)
            return Result.Success(); // already gone — logout is idempotent, not an error

        session.Revoke(timeProvider.GetUtcNow());
        await sessionRepository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<StaffPrincipal>> ValidateAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        var session = await sessionRepository.FindByTokenHashAsync(tokenGenerator.HashOf(rawToken), cancellationToken);
        if (session is null || !session.IsValid(timeProvider.GetUtcNow()))
            return Result.Failure<StaffPrincipal>(StaffUserErrors.SessionExpiredOrRevoked);

        return Result.Success(new StaffPrincipal(session.StaffUserId, session.Role));
    }
}
