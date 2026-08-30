namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// Rewrites the <c>enrollment_token</c> line inside a published agent package's own
/// <c>config.toml</c> entry to whatever value is currently valid, leaving every other entry (the
/// binary, plists, install/uninstall scripts) byte-for-byte identical to what was published.
///
/// The token rotates independently of — and typically far more often than — the agent build
/// itself, so baking it into the archive at publish time would mean a download going stale the
/// moment it rotates. Doing this substitution on every download request instead means the
/// download is always current without ever needing a republish just because the token changed —
/// see <c>AgentPackagesController.Download</c> and <see cref="IAgentEnrollmentOptions"/>.
/// </summary>
public interface IAgentPackageArchiveRewriter
{
    /// <summary>Returns a new gzip+tar stream, positioned at 0, with
    /// <paramref name="enrollmentToken"/> substituted into the archive's <c>config.toml</c> entry
    /// (blank if null/empty) — or the archive unchanged if it has no such entry.</summary>
    Task<Stream> WithEnrollmentToken(Stream sourceGzipTar, string? enrollmentToken, CancellationToken cancellationToken);
}
