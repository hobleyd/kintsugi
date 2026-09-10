namespace Kintsugi.Infrastructure.Vulnerabilities;

/// <summary>
/// Paces requests to NVD so this server stays inside the published limit: 5 requests per rolling
/// 30 seconds anonymously, 50 with an API key.
/// </summary>
/// <remarks>
/// <para>
/// <b>A singleton, and that is the whole point.</b> NVD counts per source address over a rolling
/// window, so the limit belongs to the server rather than to a request, a scope or a typed-client
/// instance. Registered alongside <see cref="NvdClient"/> in <c>DependencyInjection</c> for the
/// same reason <c>VantaAccessTokenProvider</c> is: two components each keeping their own tally
/// would each stay under the limit and together sail past it, and NVD answers that with 403s that
/// read like an authentication failure.
/// </para>
/// <para>
/// It follows that the assessment loop and a reviewer clicking "Search" on the mapping screen
/// share one allowance. That is correct — they share one address — and it is why an assessment run
/// is bounded rather than greedy: a run holding the window open for an hour would make the mapping
/// screen unusable for as long as it lasted.
/// </para>
/// <para>
/// The implementation is a plain timestamp ring rather than a token bucket because the limit
/// itself is expressed as a rolling window, and a bucket refilling at the average rate would
/// permit a burst NVD then refuses.
/// </para>
/// </remarks>
public class NvdRateLimiter
{
    /// <summary>The window NVD measures over, plus a second of slack: the two clocks are not the
    /// same clock, and being refused costs far more than waiting slightly longer.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(31);

    private const int AnonymousLimit = 5;
    private const int KeyedLimit = 50;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTimeOffset> _recent = new();

    /// <summary>
    /// Returns once another request may be sent, waiting if the window is full. Serialized on a
    /// semaphore so two callers cannot both look at a window with one slot left and both take it.
    /// </summary>
    public async Task WaitAsync(bool hasApiKey, CancellationToken cancellationToken)
    {
        var limit = hasApiKey ? KeyedLimit : AnonymousLimit;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                var now = DateTimeOffset.UtcNow;

                while (_recent.Count > 0 && now - _recent.Peek() >= Window)
                {
                    _recent.Dequeue();
                }

                // The limit is read on every pass rather than captured, because an administrator
                // can add a key mid-run: an anonymous caller already waiting should be released by
                // the larger allowance rather than serving out the smaller one.
                if (_recent.Count < limit)
                {
                    _recent.Enqueue(now);
                    return;
                }

                var wait = Window - (now - _recent.Peek());
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
