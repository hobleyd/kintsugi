using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Vulnerabilities.Commands.ConfirmCpeMapping;

public class ConfirmCpeMappingCommandHandler : IRequestHandler<ConfirmCpeMappingCommand, Unit>
{
    private readonly IVulnerabilityRepository _repository;
    private readonly IVulnerabilitySettingsProvider _settingsProvider;
    private readonly INvdClient _nvdClient;
    private readonly IUnitOfWork _unitOfWork;

    public ConfirmCpeMappingCommandHandler(
        IVulnerabilityRepository repository,
        IVulnerabilitySettingsProvider settingsProvider,
        INvdClient nvdClient,
        IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _settingsProvider = settingsProvider;
        _nvdClient = nvdClient;
        _unitOfWork = unitOfWork;
    }

    public async Task<Unit> Handle(ConfirmCpeMappingCommand request, CancellationToken cancellationToken)
    {
        var mapping = await _repository.GetMappingAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"No CPE mapping with id {request.Id}.");

        // Checked on confirmation as well as on suggestion, because a reviewer may have typed a
        // correction by hand and a typo here silently attributes another product's CVEs to this
        // one. It is one HTTP call against a decision that stands until somebody changes it.
        var settings = await _settingsProvider.GetAsync(cancellationToken);
        var matchString = CpeMapping.ToCpeMatchString(mapping.Part, request.Vendor, request.Product);

        if (!await _nvdClient.CpeExistsAsync(matchString, settings.NvdApiKey, cancellationToken))
        {
            throw new ConflictException(
                $"NVD's CPE dictionary contains no product matching {request.Vendor}:{request.Product}. "
                + "Search the dictionary for the right vendor and product, or mark this application as not applicable.");
        }

        // Manual means "a human entered this pair", not "a human pressed a button" — the Confidence
        // column reads this field and says where the pair came from. Accepting a suggestion
        // unchanged leaves its own source in place, which is what ConfirmCpeMappingsCommandHandler
        // does for a bulk accept; only an edit makes it a hand-entered pair. Without this the two
        // routes answer differently for the same action, because the queue's row tick submits
        // exactly what the row already carries.
        var unchanged = mapping.Vendor == request.Vendor
            && mapping.Product == request.Product
            && mapping.SuggestionSource != CpeSuggestionSource.None;

        mapping.Confirm(
            request.Vendor,
            request.Product,
            unchanged ? mapping.SuggestionSource : CpeSuggestionSource.Manual);

        // Re-pointing an already-confirmed mapping invalidates everything stored under it: those
        // findings describe the product it used to claim to be. Dropped rather than left to be
        // overwritten, because the next run only re-queries versions that are still installed and
        // a stale one would otherwise linger indefinitely under the new product's name.
        if (mapping.Repointed)
        {
            _repository.RemoveAssessments(await _repository.GetAssessmentsForMappingAsync(mapping.Id, cancellationToken));
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}
