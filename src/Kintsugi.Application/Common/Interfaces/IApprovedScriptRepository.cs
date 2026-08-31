using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IApprovedScriptRepository
{
    /// <summary>The stored entry for one (content, signer) pair, or null — the key an import
    /// upserts against.</summary>
    Task<ApprovedScript?> GetAsync(string sha256, string signerFingerprint, CancellationToken cancellationToken);

    Task AddAsync(ApprovedScript approvedScript, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApprovedScript>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Every distinct script content that has been approved by anyone, as a set of hashes —
    /// what blessing compares a local row's own script hash against, without pulling every script's
    /// text into memory to do it.</summary>
    Task<IReadOnlyCollection<string>> GetApprovedContentHashesAsync(CancellationToken cancellationToken);

    /// <summary>Approved entries offered for one (application, bucket) an upgrade path hasn't
    /// resolved — the adoption candidates the Upgrade Scripts page lists.</summary>
    Task<IReadOnlyList<ApprovedScript>> GetForApplicationAsync(
        string applicationName, string platformBucket, CancellationToken cancellationToken);
}
