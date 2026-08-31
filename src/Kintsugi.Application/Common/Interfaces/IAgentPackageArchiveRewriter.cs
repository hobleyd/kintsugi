namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// Rewrites one line of a published agent package's own <c>config.toml</c> entry, leaving every
/// other entry (the binary, plists/units, install/uninstall scripts) byte-for-byte identical to
/// what was published.
///
/// The two values rewritten here are rewritten at deliberately different moments, and the
/// difference is the point:
/// <list type="bullet">
/// <item><description><see cref="WithApiBaseUrl"/> runs once, at <em>import</em> time, when a
/// build is pulled off GitHub and republished onto this server (see
/// <c>ImportAgentPackagesFromSourceCommandHandler</c>). The CI-built archive carries the
/// placeholder <c>kintsugi.example.com</c>, since the server's real address is deployment detail
/// that must never appear in a tracked file. Baking the address in at import means the stored
/// bytes — and therefore the checksum signed over them — already describe this server, so an
/// enrolled agent's byte-identical self-update download still verifies.</description></item>
/// <item><description><see cref="WithEnrollmentToken"/> runs on every anonymous <em>download</em>
/// instead. The token rotates independently of — and typically far more often than — the agent
/// build, so baking it in would mean a download going stale the moment it rotated. Doing it per
/// request means a rotation never needs a republish. See <c>AgentPackagesController.Download</c>
/// and <see cref="IAgentEnrollmentOptions"/>.</description></item>
/// </list>
///
/// The two compose: a download of an imported package runs the token pass over an archive the
/// URL pass already touched, and neither pass disturbs the other's line.
/// </summary>
public interface IAgentPackageArchiveRewriter
{
    /// <summary>Returns a new gzip+tar stream, positioned at 0, with
    /// <paramref name="enrollmentToken"/> substituted into the archive's <c>config.toml</c> entry
    /// (blank if null/empty) — or the archive unchanged if it has no such entry.</summary>
    Task<Stream> WithEnrollmentToken(Stream sourceGzipTar, string? enrollmentToken, CancellationToken cancellationToken);

    /// <summary>Returns a new gzip+tar stream, positioned at 0, with <paramref name="apiBaseUrl"/>
    /// substituted into the archive's <c>config.toml</c> entry — or the archive unchanged if it
    /// has no such entry.</summary>
    Task<Stream> WithApiBaseUrl(Stream sourceGzipTar, string apiBaseUrl, CancellationToken cancellationToken);
}
