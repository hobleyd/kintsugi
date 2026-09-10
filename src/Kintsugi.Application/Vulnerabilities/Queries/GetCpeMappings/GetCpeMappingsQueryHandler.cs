using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Vulnerabilities.Queries.GetCpeMappings;

/// <summary>The mapping queue: everything the fleet has installed, and how far each has got
/// towards being assessable.</summary>
public record GetCpeMappingsQuery : IRequest<IReadOnlyList<CpeMappingDto>>;

public class GetCpeMappingsQueryHandler : IRequestHandler<GetCpeMappingsQuery, IReadOnlyList<CpeMappingDto>>
{
    private readonly IVulnerabilityRepository _repository;

    public GetCpeMappingsQueryHandler(IVulnerabilityRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<CpeMappingDto>> Handle(GetCpeMappingsQuery request, CancellationToken cancellationToken)
    {
        var mappings = await _repository.GetMappingsAsync(cancellationToken);

        var applicationHostCounts = (await _repository.GetInstalledApplicationSubjectsAsync(cancellationToken))
            .ToDictionary(s => s.Key, s => s.HostCount);

        // Operating systems are counted and versioned from the per-host facts rather than from the
        // distinct reported strings, because two hosts reporting the same "Windows 11 Pro 23H2
        // (22631)" can be at different update revisions and are two different questions for NVD.
        var osFacts = await _repository.GetOperatingSystemFactsAsync(cancellationToken);
        var osHostCounts = new Dictionary<string, int>();
        var osVersions = new Dictionary<string, HashSet<string>>();
        var osReasons = new Dictionary<string, string>();

        foreach (var host in osFacts)
        {
            var derived = OperatingSystemSubject.Derive(host.OperatingSystem, host.OperatingSystemId, host.OperatingSystemVersionId);
            if (derived is null)
            {
                continue;
            }

            osHostCounts[derived.SubjectKey] = osHostCounts.GetValueOrDefault(derived.SubjectKey) + 1;

            if (derived.Version is not null)
            {
                if (!osVersions.TryGetValue(derived.SubjectKey, out var versions))
                {
                    versions = new HashSet<string>();
                    osVersions[derived.SubjectKey] = versions;
                }

                versions.Add(derived.Version);
            }
            else if (derived.UnassessableReason is not null)
            {
                // First reason wins. Every host under one subject key that cannot be assessed is
                // unassessable for the same reason, since the reason is a property of what the
                // agent reports rather than of the machine.
                osReasons.TryAdd(derived.SubjectKey, derived.UnassessableReason);
            }
        }

        var result = new List<CpeMappingDto>(mappings.Count);

        foreach (var mapping in mappings)
        {
            var isOs = mapping.SubjectKind == CpeSubjectKind.OperatingSystem;

            var versions = isOs
                ? osVersions.TryGetValue(mapping.SubjectKey, out var found) ? found.OrderBy(v => v).ToList() : new List<string>()
                : (await _repository.GetInstalledVersionsForApplicationAsync(mapping.SubjectKey, cancellationToken)).OrderBy(v => v).ToList();

            var assessments = await _repository.GetAssessmentsForMappingAsync(mapping.Id, cancellationToken);

            result.Add(new CpeMappingDto(
                mapping.Id,
                mapping.SubjectKind,
                mapping.SubjectKey,
                mapping.DisplayName,
                mapping.Vendor,
                mapping.Product,
                mapping.Status,
                mapping.SuggestionSource,
                mapping.SuggestionNotes,
                mapping.ConfirmedAtUtc,
                isOs ? osHostCounts.GetValueOrDefault(mapping.SubjectKey) : applicationHostCounts.GetValueOrDefault(mapping.SubjectKey),
                versions,
                assessments.Sum(a => a.MatchCount),
                assessments.Sum(a => a.KnownExploitedCount),
                assessments.Count == 0 ? null : assessments.Max(a => a.LastAssessedUtc),
                // The most recent failure across this subject's versions, so a product NVD has
                // withdrawn shows its reason on the row rather than being silently stuck.
                assessments.Where(a => a.LastError is not null).OrderByDescending(a => a.LastAssessedUtc).FirstOrDefault()?.LastError,
                isOs ? osReasons.GetValueOrDefault(mapping.SubjectKey) : null));
        }

        // Most widely installed first, so the confirmations that buy the most coverage are the
        // ones at the top of the queue.
        return result
            .OrderByDescending(m => m.Status == CpeMappingStatus.Suggested)
            .ThenByDescending(m => m.HostCount)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
