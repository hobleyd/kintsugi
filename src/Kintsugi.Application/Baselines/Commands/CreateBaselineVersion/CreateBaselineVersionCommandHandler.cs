using FluentValidation;
using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Baselines.Commands.CreateBaselineVersion;

public class CreateBaselineVersionCommandHandler : IRequestHandler<CreateBaselineVersionCommand, CreatedBaselineVersionDto>
{
    private readonly IBaselineRepository _baselines;
    private readonly IUnitOfWork _unitOfWork;

    public CreateBaselineVersionCommandHandler(IBaselineRepository baselines, IUnitOfWork unitOfWork)
    {
        _baselines = baselines;
        _unitOfWork = unitOfWork;
    }

    public async Task<CreatedBaselineVersionDto> Handle(CreateBaselineVersionCommand request, CancellationToken cancellationToken)
    {
        var baseline = await _baselines.GetAsync(request.BaselineId, cancellationToken)
            ?? throw new NotFoundException($"No baseline with id '{request.BaselineId}'.");

        var versions = await _baselines.GetVersionsAsync(new[] { baseline.Id }, cancellationToken);
        var number = versions.Count == 0 ? 1 : versions.Max(v => v.Number) + 1;
        var version = BaselineVersionFactory.Create(baseline, number, request.Items, request.CreatedBy);

        await _baselines.AddVersionAsync(version, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new CreatedBaselineVersionDto(baseline.Id, version.Id, version.Number, version.DocumentSha256);
    }
}
