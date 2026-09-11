using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Vulnerabilities.Commands.ResetCpeMappings;

public class ResetCpeMappingsCommandHandler : IRequestHandler<ResetCpeMappingsCommand, BulkCpeMappingResultDto>
{
    private readonly IVulnerabilityRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ResetCpeMappingsCommandHandler(IVulnerabilityRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<BulkCpeMappingResultDto> Handle(
        ResetCpeMappingsCommand request, CancellationToken cancellationToken)
    {
        var mappings = await _repository.GetMappingsByIdsAsync(request.Ids, cancellationToken);
        var found = mappings.ToDictionary(m => m.Id);
        var skipped = new List<SkippedCpeMappingDto>();
        var applied = 0;

        foreach (var id in request.Ids.Distinct())
        {
            if (!found.TryGetValue(id, out var mapping))
            {
                skipped.Add(new SkippedCpeMappingDto(id, "(removed)", "This subject no longer exists."));
                continue;
            }

            if (mapping.Status == CpeMappingStatus.Unmapped)
            {
                skipped.Add(new SkippedCpeMappingDto(id, mapping.DisplayName, "Already in the queue."));
                continue;
            }

            // Per row, for the reason ResetCpeMappingCommandHandler does it: an unmapped subject
            // asserts nothing, so the findings stored under the mapping it used to have must not
            // outlive it.
            _repository.RemoveAssessments(await _repository.GetAssessmentsForMappingAsync(id, cancellationToken));
            mapping.Reset();
            applied++;
        }

        if (applied > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new BulkCpeMappingResultDto(applied, skipped);
    }
}
