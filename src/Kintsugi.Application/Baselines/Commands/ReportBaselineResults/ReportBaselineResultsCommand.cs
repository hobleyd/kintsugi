using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Baselines.Commands.ReportBaselineResults;

/// <summary>
/// An agent's report of what it read (<c>POST /api/baseline-results</c>), sent after every check even
/// when nothing changed — an empty report is what moves "last checked". Mirrored by hand by
/// <c>BaselineResultsRequest</c> in <c>clients/windows-agent/src/baseline.rs</c>, Windows only in this step.
/// </summary>
public record ReportBaselineResultsCommand(string SerialNumber, IReadOnlyList<BaselineResultInput> Baselines)
    : IRequest, IAgentScopedRequest;

/// <param name="VersionId">Taken by the agent from inside the signed document, not from the envelope.</param>
/// <param name="Items">Only the settings whose state or observed value differs from what the server
/// handed the agent.</param>
/// <param name="Facts">Only the facts whose value differs.</param>
public record BaselineResultInput(Guid VersionId, IReadOnlyList<ItemResultInput> Items, IReadOnlyList<FactResultInput> Facts);

/// <param name="State">A <c>BaselineItemState</c> member name.</param>
public record ItemResultInput(string Key, string State, string? Observed, string? Detail);

public record FactResultInput(string Key, string? Value);
