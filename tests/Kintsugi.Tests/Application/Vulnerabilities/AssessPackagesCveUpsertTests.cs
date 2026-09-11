using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Vulnerabilities.Commands.RunVulnerabilityAssessment;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Tests.Application.Vulnerabilities;

/// <summary>
/// The package stage upserts each CVE once per <b>batch</b>, not once per package.
/// </summary>
/// <remarks>
/// <para>
/// It has to, because the save is per batch. <c>UpsertCveRowsAsync</c> asks the database whether a
/// CVE row exists, and a query cannot see rows the current transaction has added and not yet
/// saved — so called once per package, two packages in one batch sharing a CVE nobody had seen
/// before each created a <c>Vulnerability</c> for it, the second broke the unique index on
/// <c>CveId</c>, and the whole batch's save failed with "An error occurred while saving the entity
/// changes."
/// </para>
/// <para>
/// The shape is the ordinary one rather than an edge case: the same source package at two
/// installed versions is two triples in one ecosystem's batch sharing nearly all of its CVEs, and
/// on a first run every CVE is new. It was three identical messages on the settings screen.
/// </para>
/// </remarks>
public class AssessPackagesCveUpsertTests
{
    private readonly Mock<IVulnerabilitySettingsProvider> _settingsProvider = new();
    private readonly Mock<IVulnerabilitySettingsRepository> _settingsRepository = new();
    private readonly Mock<IVulnerabilityRepository> _repository = new() { DefaultValue = DefaultValue.Empty };
    private readonly Mock<IKevCatalogClient> _kevCatalogClient = new();
    private readonly Mock<INvdClient> _nvdClient = new() { DefaultValue = DefaultValue.Empty };
    private readonly Mock<IOsvClient> _osvClient = new() { DefaultValue = DefaultValue.Empty };
    private readonly Mock<IInstalledPackageRepository> _packageRepository = new() { DefaultValue = DefaultValue.Empty };
    private readonly Mock<ICpeSuggestionClient> _cpeSuggestionClient = new() { DefaultValue = DefaultValue.Empty };
    private readonly Mock<IAiAgentSettingsRepository> _aiAgentSettingsRepository = new() { DefaultValue = DefaultValue.Empty };
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IVulnerabilityRunProgress> _progress = new();

    private readonly List<Vulnerability> _added = new();

    public AssessPackagesCveUpsertTests()
    {
        _settingsProvider
            .Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            // AutoSuggestCpes off, so nothing but the package stage does any work here.
            .ReturnsAsync(new VulnerabilitySettingsSnapshot(true, null, 24, 250, 4000, false, null, null));

        _kevCatalogClient
            .Setup(c => c.GetCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KevCatalog(null, null, Array.Empty<KevEntry>()));

        // The database's answer, which is what the handler sees: rows added in this batch and not
        // yet saved are invisible to it, exactly as a real query is.
        _repository
            .Setup(r => r.GetVulnerabilitiesByCveIdAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, Vulnerability>(StringComparer.OrdinalIgnoreCase));

        _repository
            .Setup(r => r.AddVulnerabilityAsync(It.IsAny<Vulnerability>(), It.IsAny<CancellationToken>()))
            .Callback<Vulnerability, CancellationToken>((v, _) => _added.Add(v))
            .Returns(Task.CompletedTask);

        // The three stages that run before the package one, answered with nothing so this test is
        // only about the fourth.
        _repository
            .Setup(r => r.GetKnownExploitedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Vulnerability>());
        _repository
            .Setup(r => r.GetMappingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<CpeMapping>());
        _repository
            .Setup(r => r.GetInstalledApplicationSubjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DiscoveredSubject>());
        _repository
            .Setup(r => r.GetOperatingSystemFactsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<HostOperatingSystemFacts>());
        _repository
            .Setup(r => r.GetInstalledVersionsBySubjectAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IReadOnlyList<string>>());
        _repository
            .Setup(r => r.GetAssessmentsDueAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<CpeAssessment>());

        // The package stage's own reconcile: nothing to add and nothing stale, so `due` below is
        // exactly what the test hands it.
        _packageRepository
            .Setup(p => p.GetDistinctPackageTriplesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PackageTriple>());
        _repository
            .Setup(r => r.GetQueuedPackageTriplesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PackageTriple>());
        _repository
            .Setup(r => r.GetOsvAdvisoriesAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, OsvAdvisory>(StringComparer.OrdinalIgnoreCase));

        // A UBUNTU-CVE- identifier names its own CVE, but an unscored CVE still has its record
        // fetched for a vector. No vector here, which is a real answer for a distribution-only
        // advisory and keeps this test off the scoring path.
        _osvClient
            .Setup(c => c.GetAdvisoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string osvId, CancellationToken _) =>
                new OsvAdvisoryRecord(new[] { OsvAdvisory.CveFromIdentifier(osvId) ?? osvId }, null));
    }

    private RunVulnerabilityAssessmentCommandHandler Handler() => new(
        _settingsProvider.Object,
        _settingsRepository.Object,
        _repository.Object,
        _kevCatalogClient.Object,
        _nvdClient.Object,
        _osvClient.Object,
        _packageRepository.Object,
        _cpeSuggestionClient.Object,
        _aiAgentSettingsRepository.Object,
        _unitOfWork.Object,
        _progress.Object);

    [Fact]
    public async Task Two_packages_in_one_batch_sharing_a_cve_create_one_vulnerability_row()
    {
        // The same source package at two installed versions — one ecosystem, so one batch, and one
        // save covering both.
        var first = PackageAssessment.Queue("Ubuntu:22.04", "openssl", "3.0.2-0ubuntu1.15");
        var second = PackageAssessment.Queue("Ubuntu:22.04", "openssl", "3.0.2-0ubuntu1.19");

        _repository
            .Setup(r => r.GetPackageAssessmentsDueAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { first, second });

        // Both affected by the same CVE. UBUNTU-CVE-… names its own CVE, so no advisory fetch is
        // involved and the test stays about the upsert.
        _osvClient
            .Setup(c => c.QueryBatchAsync(It.IsAny<IReadOnlyList<OsvPackageQuery>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<OsvPackageQuery> queries, CancellationToken _) => queries
                .Select(q => new OsvQueryResult(q, new[] { "UBUNTU-CVE-2024-2511" }))
                .ToList());

        await Handler().Handle(new RunVulnerabilityAssessmentCommand(), CancellationToken.None);

        // One row, not two. Two is the unique-index violation on Vulnerabilities.CveId.
        Assert.Equal(new[] { "CVE-2024-2511" }, _added.Select(v => v.CveId).ToArray());

        // And both packages still get their own match rows against it — the fix must not collapse
        // two packages' findings into one.
        _repository.Verify(
            r => r.ReplacePackageMatchesAsync(first.Id, It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1), It.IsAny<CancellationToken>()),
            Times.Once());
        _repository.Verify(
            r => r.ReplacePackageMatchesAsync(second.Id, It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1), It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A batch whose save failed must not ride along on the next batch's successful one.
    /// </summary>
    /// <remarks>
    /// Its <c>RecordAssessment</c> mutations are still tracked as Modified, and
    /// <c>ReplacePackageMatchesAsync</c> deletes the old match rows with <c>ExecuteDelete</c>,
    /// which commits outside the unit of work — so a later save would write a match count for rows
    /// that were deleted and never re-inserted. A count with no findings behind it is the clean
    /// bill of health this whole feature exists to refuse.
    /// </remarks>
    [Fact]
    public async Task A_failed_batch_save_detaches_that_batch()
    {
        var assessment = PackageAssessment.Queue("Ubuntu:22.04", "openssl", "3.0.2-0ubuntu1.15");

        _repository
            .Setup(r => r.GetPackageAssessmentsDueAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { assessment });

        _osvClient
            .Setup(c => c.QueryBatchAsync(It.IsAny<IReadOnlyList<OsvPackageQuery>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<OsvPackageQuery> queries, CancellationToken _) => queries
                .Select(q => new OsvQueryResult(q, new[] { "UBUNTU-CVE-2024-2511" }))
                .ToList());

        // The stages before this one save too, and they must succeed — so the failure is keyed on
        // having reached the batch's own work rather than on counting saves, which would move with
        // any change to the stages above.
        var inBatch = false;
        _repository
            .Setup(r => r.ReplacePackageMatchesAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .Callback(() => inBatch = true)
            .Returns(Task.CompletedTask);

        _unitOfWork
            .Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(_ => inBatch
                ? throw new InvalidOperationException(
                    "An error occurred while saving the entity changes. See the inner exception for details.",
                    new InvalidOperationException("duplicate key value violates unique constraint \"IX_vulnerabilities_CveId\""))
                : Task.FromResult(0));

        var result = await Handler().Handle(new RunVulnerabilityAssessmentCommand(), CancellationToken.None);

        _repository.Verify(
            r => r.DetachPackageAssessments(It.Is<IReadOnlyCollection<PackageAssessment>>(b => b.Contains(assessment))),
            Times.Once());

        // And the reported problem carries what the database said, not just the wrapper's fixed
        // sentence — this string is the only account of a failed run anybody gets.
        Assert.Contains("IX_vulnerabilities_CveId", result.Message);
    }
}
