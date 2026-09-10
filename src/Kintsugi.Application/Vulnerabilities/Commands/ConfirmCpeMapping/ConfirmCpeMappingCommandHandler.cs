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

        mapping.Confirm(request.Vendor, request.Product, CpeSuggestionSource.Manual);

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
