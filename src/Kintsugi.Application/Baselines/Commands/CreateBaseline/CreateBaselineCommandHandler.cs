using FluentValidation;
using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Baselines.Commands.CreateBaseline;

public class CreateBaselineCommandHandler : IRequestHandler<CreateBaselineCommand, CreatedBaselineVersionDto>
{
    private readonly IBaselineRepository _baselines;
    private readonly IUnitOfWork _unitOfWork;

    public CreateBaselineCommandHandler(IBaselineRepository baselines, IUnitOfWork unitOfWork)
    {
        _baselines = baselines;
        _unitOfWork = unitOfWork;
    }

    public async Task<CreatedBaselineVersionDto> Handle(CreateBaselineCommand request, CancellationToken cancellationToken)
    {
        if (await _baselines.GetByNameAsync(request.Name.Trim(), cancellationToken) is not null)
        {
            throw new ConflictException($"A baseline named '{request.Name.Trim()}' already exists. Add a version to it instead.");
        }

        var baseline = Baseline.Create(request.Name, request.CreatedBy);
        var version = BaselineVersionFactory.Create(baseline, 1, request.Items, request.CreatedBy);

        await _baselines.AddAsync(baseline, cancellationToken);
        await _baselines.AddVersionAsync(version, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new CreatedBaselineVersionDto(baseline.Id, version.Id, version.Number, version.DocumentSha256);
    }
}
