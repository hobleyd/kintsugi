using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Vulnerabilities.Commands.ResetCpeMapping;

public class ResetCpeMappingCommandHandler : IRequestHandler<ResetCpeMappingCommand, Unit>
{
    private readonly IVulnerabilityRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ResetCpeMappingCommandHandler(IVulnerabilityRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Unit> Handle(ResetCpeMappingCommand request, CancellationToken cancellationToken)
    {
        var mapping = await _repository.GetMappingAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"No CPE mapping with id {request.Id}.");

        // Same reasoning as marking not-applicable: an unmapped subject asserts nothing, so the
        // findings it used to carry must not outlive the mapping that produced them.
        _repository.RemoveAssessments(await _repository.GetAssessmentsForMappingAsync(mapping.Id, cancellationToken));

        mapping.Reset();
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}
