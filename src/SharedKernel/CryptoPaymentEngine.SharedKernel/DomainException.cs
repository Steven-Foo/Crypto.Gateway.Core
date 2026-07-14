namespace CryptoPaymentEngine.SharedKernel;

/// <summary>
/// Thrown only for invariant violations that should never happen if calling code is correct
/// (a truly unexpected state). Expected/business failures use <see cref="Result"/>, not exceptions.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message)
    {
    }

    public DomainException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
