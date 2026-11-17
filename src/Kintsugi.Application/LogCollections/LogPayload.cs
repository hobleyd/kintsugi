using System.IO.Compression;
using System.Text.Json;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.LogCollections;

/// <summary>
/// The log an agent sends, checked and rewritten before it is kept. The agent is trusted to be that host's,
/// not to be right: what is stored is a fresh, bounded copy built from fields this server understands,
/// never the bytes that arrived.
/// </summary>
public static class LogPayload
{
    public const int MaxEntries = 20_000;
    public const int MaxSourceLength = 120;
    public const int MaxMessageLength = 2_000;

    /// <summary>The size an agent may send, gzip. Mirrors the agents' own cap.</summary>
    public const int MaxCompressedBytes = 5 * 1024 * 1024;

    /// <summary>What it may expand to, so a small upload cannot become a huge one in memory.</summary>
    public const int MaxDecompressedBytes = 64 * 1024 * 1024;

    private static readonly HashSet<string> Severities = new(StringComparer.Ordinal) { "Critical", "Error", "Warning", "Info" };

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private record Uploaded(List<UploadedEntry>? Entries, DateTimeOffset? TruncatedBefore);

    private record UploadedEntry(DateTimeOffset T, string? Sev, string? Src, string? Msg);

    private record Stored(List<LogEntryDto> Entries);

    public record Sanitised(byte[] Content, int Count, DateTimeOffset? TruncatedBefore);

    /// <summary>Checks and rewrites an upload. Entries with a bad time or severity are dropped, text is cut to its
    /// limits, the newest <see cref="MaxEntries"/> are kept.</summary>
    public static Sanitised Sanitise(byte[] gzip, int periodDays, DateTimeOffset now)
    {
        if (gzip.Length == 0 || gzip.Length > MaxCompressedBytes)
        {
            throw new DomainException($"The log must be between 1 byte and {MaxCompressedBytes / 1024 / 1024} MB compressed.");
        }

        Uploaded? uploaded;
        try
        {
            using var input = new MemoryStream(gzip);
            using var zip = new GZipStream(input, CompressionMode.Decompress);
            using var bounded = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = zip.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (bounded.Length + read > MaxDecompressedBytes)
                {
                    throw new DomainException("The log is too large once expanded.");
                }

                bounded.Write(buffer, 0, read);
            }

            bounded.Position = 0;
            uploaded = JsonSerializer.Deserialize<Uploaded>(bounded, Options);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            throw new DomainException("The log is not a gzip of the expected JSON.");
        }

        var earliest = now.AddDays(-(periodDays + 5));
        var latest = now.AddMinutes(10);
        var entries = (uploaded?.Entries ?? [])
            .Where(e => e.T >= earliest && e.T <= latest && e.Sev is not null && Severities.Contains(e.Sev))
            .OrderByDescending(e => e.T)
            .Take(MaxEntries)
            .Select(e => new LogEntryDto(e.T.ToUniversalTime(), e.Sev!, Cut(e.Src, MaxSourceLength), Cut(e.Msg, MaxMessageLength)))
            .OrderBy(e => e.T)
            .ToList();

        return new Sanitised(Compress(new Stored(entries)), entries.Count, uploaded?.TruncatedBefore?.ToUniversalTime());
    }

    public static IReadOnlyList<LogEntryDto> Read(byte[]? content)
    {
        if (content is null || content.Length == 0)
        {
            return [];
        }

        using var input = new MemoryStream(content);
        using var zip = new GZipStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Stored>(zip, Options)?.Entries ?? [];
    }

    private static byte[] Compress(Stored stored)
    {
        using var output = new MemoryStream();
        using (var zip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            JsonSerializer.Serialize(zip, stored, Options);
        }

        return output.ToArray();
    }

    private static string Cut(string? text, int max)
    {
        var value = (text ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }
}
