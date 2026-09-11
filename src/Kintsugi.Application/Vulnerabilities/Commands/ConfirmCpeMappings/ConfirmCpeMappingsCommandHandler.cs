using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Vulnerabilities.Commands.ConfirmCpeMappings;

public class ConfirmCpeMappingsCommandHandler : IRequestHandler<ConfirmCpeMappingsCommand, BulkCpeMappingResultDto>
{
    private readonly IVulnerabilityRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ConfirmCpeMappingsCommandHandler(IVulnerabilityRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<BulkCpeMappingResultDto> Handle(
        ConfirmCpeMappingsCommand request, CancellationToken cancellationToken)
    {
        var mappings = await _repository.GetMappingsByIdsAsync(request.Ids, cancellationToken);
        var found = mappings.ToDictionary(m => m.Id);
        var skipped = new List<SkippedCpeMappingDto>();
        var applied = 0;

        foreach (var id in request.Ids.Distinct())
        {
            if (!found.TryGetValue(id, out var mapping))
            {
                // Not an error for the batch: another session may have removed the subject between
                // this screen's last read and this click, and the rest of the selection is still
                // perfectly confirmable.
                skipped.Add(new SkippedCpeMappingDto(id, "(removed)", "This subject no longer exists."));
                continue;
            }

            if (mapping.Vendor is null || mapping.Product is null)
            {
                skipped.Add(new SkippedCpeMappingDto(
                    id,
                    mapping.DisplayName,
                    "Nothing is proposed for it yet — open the row and search NVD's dictionary."));
                continue;
            }

            if (mapping.Status == CpeMappingStatus.Confirmed)
            {
                skipped.Add(new SkippedCpeMappingDto(id, mapping.DisplayName, "Already confirmed."));
                continue;
            }

            // No dictionary call, and this is the whole reason bulk confirmation is usable at all.
            // Every stored suggestion was checked against NVD's dictionary before it was written,
            // so re-asking here would spend one NVD request per ticked row against an allowance of
            // five per thirty seconds, to re-learn what this system established when it wrote the
            // row.
            //
            // That rests on one fact worth naming, because nothing enforces it:
            // RunVulnerabilityAssessmentCommandHandler's suggestion stage is the *only* caller of
            // CpeMapping.Suggest in this codebase, and it calls INvdClient.CpeExistsAsync first and
            // discards anything the dictionary does not contain. A second path that stored a
            // suggestion without that check would make this loop accept an unverified pair — which
            // is the "attribute another product's CVEs to yours" failure the whole review step
            // exists to prevent. Add the check there, or make this ask.
            //
            // The source is carried through rather than overwritten: nobody typed anything here,
            // and Manual means a pair a human entered — see ConfirmCpeMappingCommandHandler, which
            // draws the same line for an unchanged pair.
            mapping.Confirm(mapping.Vendor, mapping.Product, mapping.SuggestionSource);
            applied++;
        }

        if (applied > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new BulkCpeMappingResultDto(applied, skipped);
    }
}
