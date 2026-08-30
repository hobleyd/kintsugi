using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths.Queries.PrepareUpgradePathScan;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathPrompt;

public class GetUpgradePathPromptQueryHandler : IRequestHandler<GetUpgradePathPromptQuery, UpgradePathPromptDto>
{
    private readonly ISender _sender;
    private readonly IUpgradePathResearchClient _researchClient;
    private readonly IUpgradePathRepository _upgradePathRepository;

    public GetUpgradePathPromptQueryHandler(ISender sender, IUpgradePathResearchClient researchClient, IUpgradePathRepository upgradePathRepository)
    {
        _sender = sender;
        _researchClient = researchClient;
        _upgradePathRepository = upgradePathRepository;
    }

    public async Task<UpgradePathPromptDto> Handle(GetUpgradePathPromptQuery request, CancellationToken cancellationToken)
    {
        // Looked up regardless of AI availability/package-manager status below, so an already
        // -successful check still shows up in the panel even when the AI agent is unreachable, or
        // now handled by a package manager. Only possible when the platform is already known — a
        // row with no upgrade path researched at all never sends one.
        var existingResult = request.Platform is not null
            ? await GetExistingResultAsync(request.ApplicationName, request.Platform, cancellationToken)
            : null;

        var plan = await _sender.Send(new PrepareUpgradePathScanQuery(), cancellationToken);

        var matching = plan.WorkItems
            .Where(item => item.ApplicationName.Equals(request.ApplicationName, StringComparison.OrdinalIgnoreCase)
                && (request.Platform is null || item.Platform.Equals(request.Platform, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.Platform, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matching.Count == 0)
        {
            return new UpgradePathPromptDto(false, request.Platform, null, $"'{request.ApplicationName}' is not currently installed on any host.", existingResult);
        }

        var researchItem = matching.FirstOrDefault(item => item.Kind == UpgradePathWorkKind.Research);
        if (researchItem is null)
        {
            // Checked before AiConfigured below: a package-manager-managed row has no AI prompt
            // regardless of whether the AI agent happens to be configured, so it should never be
            // reported as blocked on that.
            return new UpgradePathPromptDto(false, matching[0].Platform, null, $"'{request.ApplicationName}' is managed by a package manager — no AI research is used for it.", existingResult);
        }

        if (!plan.AiConfigured)
        {
            return new UpgradePathPromptDto(false, researchItem.Platform, null, "The AI agent is not configured or not enabled — configure it under Settings first.", existingResult);
        }

        var prompt = _researchClient.BuildDefaultPrompt(
            new UpgradePathScriptGenerationRequest(researchItem.ApplicationName, researchItem.Platform, researchItem.KnownVersions, researchItem.ApplicationIdentifier));

        existingResult ??= await GetExistingResultAsync(researchItem.ApplicationName, researchItem.Platform, cancellationToken);

        return new UpgradePathPromptDto(true, researchItem.Platform, prompt, null, existingResult);
    }

    private async Task<UpgradePathResultDto?> GetExistingResultAsync(string applicationName, string platform, CancellationToken cancellationToken)
    {
        var existing = await _upgradePathRepository.GetAsync(applicationName, platform, cancellationToken);
        if (existing is null || existing.Status != UpgradePathStatus.Found)
        {
            return null;
        }

        return new UpgradePathResultDto(
            existing.ApplicationName, existing.Platform, existing.Status, existing.LatestVersion, existing.Method,
            existing.DownloadUrl, existing.Command, existing.Instructions, existing.SourceUrl, existing.Notes, existing.CheckedUtc, existing.Script,
            existing.ScriptSignature is not null);
    }
}
