namespace CryptoPaymentEngine.SharedKernel;

public enum ErrorType
{
    Failure,
    Validation,
    NotFound,
    Conflict,
    Unauthorized,
    /// <summary>The caller is authenticated and the target exists, but this specific action on this specific
    /// target is not permitted — distinct from <see cref="Unauthorized"/> (missing/invalid credentials
    /// entirely). Maps to HTTP 403 where a host's mapper distinguishes it.</summary>
    Forbidden,
}

public sealed record Error(string Code, string Message, ErrorType Type = ErrorType.Failure)
{
    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);
    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);
    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);
    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);
    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);
    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);
}
