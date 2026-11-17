using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// One line on a host's activity timeline: something the server learned about patching that host.
/// </summary>
/// <remarks>
/// <para>
/// Append-only history, which is what the rest of the model is not: <see cref="InstalledApplication"/>
/// holds only the current version, and <see cref="PatchFailure"/> is one row per application with a
/// counter. This is the "what happened, when, and who did it" record. It is also the table the
/// Autonomy view is meant to read, so an agent-reported event can be added later as a new
/// <see cref="HostEventKind"/> rather than as a second table telling the same story.
/// </para>
/// <para>
/// Repeats of one failure are folded rather than appended (<see cref="RecordRepeat"/>): a host
/// that fails the same install every cycle would otherwise write a row per cycle for as long as it
/// stays broken. Old rows are removed after the retention set in <see cref="HostActivitySettings"/>.
/// </para>
/// </remarks>
public class HostEvent : BaseEntity
{
    /// <summary>Longest <see cref="Detail"/> kept. A failing install's output can run to
    /// megabytes; the start and the end are the useful parts.</summary>
    public const int MaxDetailLength = 4000;

    public Guid HostId { get; private set; }

    public DateTimeOffset OccurredUtc { get; private set; }

    public HostEventKind Kind { get; private set; }

    /// <summary>What it was about: an application name, or "Windows", "macOS" or "Linux".</summary>
    public string Subject { get; private set; } = default!;

    public string? FromVersion { get; private set; }

    public string? ToVersion { get; private set; }

    /// <summary>The signed-in administrator who caused it, or null when the host's agent did.
    /// Read from the session by the controller, never from the request body.</summary>
    public string? Actor { get; private set; }

    /// <summary>Captured output or an explanation, cut to <see cref="MaxDetailLength"/>.</summary>
    public string? Detail { get; private set; }

    /// <summary>How many times this happened. Greater than one only for a folded failure.</summary>
    public int Count { get; private set; } = 1;

    private HostEvent()
    {
    }

    public static HostEvent Record(
        Guid hostId,
        HostEventKind kind,
        string subject,
        DateTimeOffset occurredUtc,
        string? actor = null,
        string? fromVersion = null,
        string? toVersion = null,
        string? detail = null) =>
        new()
        {
            HostId = hostId,
            Kind = kind,
            Subject = subject,
            OccurredUtc = occurredUtc,
            Actor = actor,
            FromVersion = fromVersion,
            ToVersion = toVersion,
            Detail = Truncate(detail),
        };

    /// <summary>The same failure again: bumps the count and moves the event to the latest try,
    /// with that try's output.</summary>
    public void RecordRepeat(DateTimeOffset occurredUtc, string? detail, string? fromVersion, string? toVersion)
    {
        Count++;
        OccurredUtc = occurredUtc;
        Detail = Truncate(detail);
        FromVersion = fromVersion;
        ToVersion = toVersion;
        MarkUpdated();
    }

    /// <summary>Keeps the first and last part of a long text, with a marker between.</summary>
    public static string? Truncate(string? text)
    {
        if (text is null || text.Length <= MaxDetailLength)
        {
            return text;
        }

        const string marker = "\n… output cut …\n";
        var half = (MaxDetailLength - marker.Length) / 2;
        return string.Concat(text.AsSpan(0, half), marker, text.AsSpan(text.Length - half));
    }
}
