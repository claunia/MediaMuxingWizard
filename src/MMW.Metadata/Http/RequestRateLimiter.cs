namespace MMW.Metadata.Http;

/// <summary>Sliding-window client-side rate limiter: at most <c>permits</c> requests per <c>window</c>.</summary>
public sealed class RequestRateLimiter : IDisposable
{
    private readonly Queue<DateTimeOffset> _stamps = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;

    /// <summary>Creates a limiter.</summary>
    /// <param name="permits">Requests allowed per window (must be positive).</param>
    /// <param name="window">Window length.</param>
    /// <param name="timeProvider">Clock (tests); defaults to the system clock.</param>
    public RequestRateLimiter(int permits, TimeSpan window, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permits);
        Permits = permits;
        Window = window;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Process-wide limiter for the iTunes Search API (Apple documents ~20 calls per minute).</summary>
    public static RequestRateLimiter ITunes { get; } = new(20, TimeSpan.FromMinutes(1));

    /// <summary>A limiter that never waits.</summary>
    public static RequestRateLimiter Unlimited { get; } = new(int.MaxValue, TimeSpan.Zero);

    /// <summary>Requests allowed per window.</summary>
    public int Permits { get; }

    /// <summary>Window length.</summary>
    public TimeSpan Window { get; }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>Waits until a request may be sent and records it.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        if (Window <= TimeSpan.Zero)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var now = _time.GetUtcNow();
                while (_stamps.Count > 0 && now - _stamps.Peek() >= Window)
                    _stamps.Dequeue();

                if (_stamps.Count < Permits)
                {
                    _stamps.Enqueue(now);
                    return;
                }

                var wait = Window - (now - _stamps.Peek());
                await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
