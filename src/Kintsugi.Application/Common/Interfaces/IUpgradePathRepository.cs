using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IUpgradePathRepository
{
    Task<UpgradePath?> GetAsync(string applicationName, string platform, CancellationToken cancellationToken);

    /// <summary>Looks up the upgrade path recorded against one application identifier. First match if
    /// more than one row somehow shares an identifier; that shouldn't normally happen since a bundle
    /// identifier is specific to one application.</summary>
    Task<UpgradePath?> GetByApplicationIdentifierAsync(string applicationIdentifier, CancellationToken cancellationToken);

    Task AddAsync(UpgradePath upgradePath, CancellationToken cancellationToken);

    /// <summary>Every row recorded for this application name, across whichever platform bucket(s)
    /// it happens to be stored under — used to find and retire a row left over under the wrong
    /// bucket after a package-manager-managed application's canonical platform changed (see
    /// <c>RegisterApplicationsCommandHandler</c>'s Homebrew seeding), rather than leaving it to sit
    /// forever as an orphaned duplicate that keeps shadowing the correct one.</summary>
    Task<IReadOnlyList<UpgradePath>> GetAllForApplicationAsync(string applicationName, CancellationToken cancellationToken);

    void Remove(UpgradePath upgradePath);

    /// <summary>The signature already recorded on some other row whose <see cref="UpgradePath.Script"/>
    /// is byte-for-byte identical, if one exists — lets a Homebrew row inherit a human's prior
    /// review the moment its own script content (see <c>HomebrewUpgradeScript.Build</c>, which
    /// produces the same text for every formula/cask sharing an isSelfUpdate case) matches one
    /// that's already been signed, rather than needing its own separate "Sign Script" review.</summary>
    Task<string?> FindExistingSignatureForScriptAsync(string script, CancellationToken cancellationToken);

    /// <summary>Every currently-unsigned row whose <see cref="UpgradePath.Script"/> is byte-for-byte
    /// identical to <paramref name="script"/> — used to propagate one freshly-signed Homebrew
    /// script's signature immediately to every other already-resolved row sharing that same content
    /// (see <c>SignUpgradePathScriptCommandHandler</c>), instead of leaving them to self-heal only
    /// the next time each one happens to get rescanned or re-registered.</summary>
    Task<IReadOnlyList<UpgradePath>> GetUnsignedRowsWithScriptAsync(string script, CancellationToken cancellationToken);

    /// <summary>Every row that carries no human-approved script signature yet — either because no
    /// script has been resolved for it at all, or because one has and nobody has reviewed it. These
    /// are what the Upgrade Scripts page offers approved content for, and the only rows adoption is
    /// ever allowed to touch (see <c>UpgradePath.AdoptApprovedScript</c>).</summary>
    Task<IReadOnlyList<UpgradePath>> GetRowsWithoutScriptSignatureAsync(CancellationToken cancellationToken);

    /// <summary>Every installed (host, application) pairing joined with its known upgrade path, if
    /// any has been researched, scoped to one host. Intended for the kintsugi-agent's own use —
    /// not for an unscoped, fleet-wide listing, which <see cref="GetSummariesAsync"/> covers instead.</summary>
    Task<IReadOnlyList<UpgradeStatusDto>> GetStatusesAsync(string serialNumber, CancellationToken cancellationToken);

    /// <summary>One row per (application, platform) upgrade path, with host counts aggregated at
    /// the database level — safe to call across a large fleet, unlike expanding to one row per host.</summary>
    Task<IReadOnlyList<UpgradePathSummaryDto>> GetSummariesAsync(CancellationToken cancellationToken);

    /// <summary>Every upgrade path currently resolved via an AI-generated script — what "Check for
    /// Updates" re-checks by running each one's own <c>--update-version</c> mode, with no AI call
    /// involved.</summary>
    Task<IReadOnlyList<UpgradePath>> GetScriptUpgradePathsAsync(CancellationToken cancellationToken);

    /// <summary>Count of installed applications with a known update available, per host — hosts
    /// with no such applications are absent from the result rather than present with a zero.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetAppUpdateCountsByHostAsync(CancellationToken cancellationToken);
}
