using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Providers.MistTrack;

/// <summary>
/// Paces outbound provider calls to the plan's per-second limit, process-wide.
///
/// <para><b>Why this is its own singleton rather than a field on the provider.</b> The provider is a typed
/// <see cref="HttpClient"/> consumer, and <c>AddHttpClient&lt;TClient, TImplementation&gt;</c> registers the
/// implementation as <b>transient</b>. So every DI scope gets a fresh provider — and a fresh gate, whose
/// "next allowed call" starts at the beginning of time. Pacing therefore held only within a single scope:
/// correct inside one worker pass, and no constraint at all between passes, between a worker and an HTTP
/// request, or between two workers running in the same process. Each would have paced itself perfectly
/// while the process as a whole breached the limit by the number of concurrent callers.</para>
///
/// <para>A breach is not a cosmetic problem. The provider answers 429, a 429 is a screening that produced
/// no verdict, and the payout gate holds a no-verdict payout for staff. So an unpaced burst converts
/// directly into a queue of held payouts.</para>
///
/// <para><b>Still only correct within one process.</b> Two instances each hold their own gate and would
/// breach the limit together. That is why the screening paths are drained under a single-flight
/// distributed lock. If screening ever runs concurrently across instances, this must become a Redis token
/// bucket — the lock is what makes an in-process limiter sufficient, and removing it silently removes the
/// pacing guarantee with it.</para>
/// </summary>
public sealed class MistTrackRateLimiter : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock;
    private readonly double _requestsPerSecond;
    private DateTimeOffset _nextAllowedCall = DateTimeOffset.MinValue;

    public MistTrackRateLimiter(IOptions<MistTrackOptions> options, TimeProvider clock)
    {
        _clock = clock;
        _requestsPerSecond = options.Value.RequestsPerSecond;
    }

    /// <summary>Waits until another call is permitted. Serialised through a semaphore so concurrent callers
    /// queue rather than all reading the same "next allowed" instant and firing together.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        if (_requestsPerSecond <= 0)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(1d / _requestsPerSecond);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _clock.GetUtcNow();
            var wait = _nextAllowedCall - now;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _clock, cancellationToken);
                now = _clock.GetUtcNow();
            }

            _nextAllowedCall = now + interval;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
