using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.HostActivity.Commands.UpdateHostActivityRetention;

public class UpdateHostActivityRetentionCommandHandler : IRequestHandler<UpdateHostActivityRetentionCommand, HostActivitySettingsDto>
{
    private readonly IHostActivitySettingsRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateHostActivityRetentionCommandHandler(IHostActivitySettingsRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<HostActivitySettingsDto> Handle(UpdateHostActivityRetentionCommand request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);
        if (settings is null)
        {
            await _repository.AddAsync(HostActivitySettings.Create(request.RetentionMonths), cancellationToken);
        }
        else
        {
            settings.Update(request.RetentionMonths);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return new HostActivitySettingsDto(request.RetentionMonths);
    }
}
