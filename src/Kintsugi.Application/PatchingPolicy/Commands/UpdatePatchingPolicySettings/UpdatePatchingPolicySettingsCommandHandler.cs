using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.PatchingPolicy.Commands.UpdatePatchingPolicySettings;

public class UpdatePatchingPolicySettingsCommandHandler : IRequestHandler<UpdatePatchingPolicySettingsCommand, PatchingPolicySettingsDto>
{
    private readonly IPatchingPolicySettingsRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public UpdatePatchingPolicySettingsCommandHandler(IPatchingPolicySettingsRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<PatchingPolicySettingsDto> Handle(UpdatePatchingPolicySettingsCommand request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);

        if (settings is null)
        {
            settings = PatchingPolicySettings.Create(request.IntervalValue, request.IntervalUnit, request.DelayValue, request.DelayUnit, request.MaxDelayCount);
            await _repository.AddAsync(settings, cancellationToken);
        }
        else
        {
            settings.Update(request.IntervalValue, request.IntervalUnit, request.DelayValue, request.DelayUnit, request.MaxDelayCount);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return PatchingPolicySettingsDto.FromEntity(settings);
    }
}
