namespace Kintsugi.Application.AiSettings;

/// <summary>One model call's token use, as the engine saw it.</summary>
/// <param name="Free">A model that runs on the server's own hardware (Ollama): its tokens are worth
/// recording and cost nothing, which is not the same as having no known price.</param>
public sealed record AiUsageEntry(string ConnectionName, string Model, long InputTokens, long OutputTokens, bool Free = false);

/// <summary>
/// Collects the token use of every model call made inside it, so the code that knows *why* the call
/// was made (which feature, which application) can record it without the engine knowing either.
/// </summary>
/// <remarks>
/// An ambient scope, not a return value, because one feature call can be several model calls (a
/// research loop makes one per tool round) and the engine returns plain text to a dozen callers.
/// <see cref="Add"/> does nothing when no scope is open, so a call made outside one — a connection
/// test, say — is simply not recorded.
/// </remarks>
public sealed class AiUsageScope : IDisposable
{
    private static readonly AsyncLocal<AiUsageScope?> Current = new();

    private readonly AiUsageScope? _outer;
    private readonly List<AiUsageEntry> _entries = new();
    private readonly object _gate = new();

    private AiUsageScope()
    {
        _outer = Current.Value;
        Current.Value = this;
    }

    public static AiUsageScope Begin() => new();

    public static void Add(AiUsageEntry entry)
    {
        var scope = Current.Value;
        if (scope is null)
        {
            return;
        }

        lock (scope._gate)
        {
            scope._entries.Add(entry);
        }
    }

    public IReadOnlyList<AiUsageEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    public void Dispose() => Current.Value = _outer;
}
