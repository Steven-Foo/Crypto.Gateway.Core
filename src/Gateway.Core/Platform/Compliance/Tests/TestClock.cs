namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Tests;

/// <summary>
/// A hand-rolled controllable clock, matching the convention the other module test projects use rather
/// than pulling in a testing package for one type. Cache expiry is measured in days here, so the tests
/// have to move time deliberately instead of waiting for it.
/// </summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public TestClock() : this(DateTimeOffset.Parse("2026-09-10T00:00:00Z"))
    {
    }

    public void Advance(TimeSpan by) => _now = _now.Add(by);

    public override DateTimeOffset GetUtcNow() => _now;
}
