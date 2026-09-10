using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Vulnerabilities.Commands.SetCpeMappingNotApplicable;

public class SetCpeMappingNotApplicableCommandHandler : IRequestHandler<SetCpeMappingNotApplicableCommand, Unit>
{
    private readonly IVulnerabilityRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public SetCpeMappingNotApplicableCommandHandler(IVulnerabilityRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Unit> Handle(SetCpeMappingNotApplicableCommand request, CancellationToken cancellationToken)
    {
        var mapping = await _repository.GetMappingAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"No CPE mapping with id {request.Id}.");

        // Any findings this subject already produced go with the decision: they were matched
        // against a product it is now saying it is not.
        _repository.RemoveAssessments(await _repository.GetAssessmentsForMappingAsync(mapping.Id, cancellationToken));

        mapping.MarkNotApplicable(request.Notes);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}
