using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.AiSettings.Commands.UpdateAiBudget;

public class UpdateAiBudgetCommandHandler : IRequestHandler<UpdateAiBudgetCommand, AiBudgetSettingsDto>
{
    private readonly IAiUsageRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateAiBudgetCommandHandler(IAiUsageRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<AiBudgetSettingsDto> Handle(UpdateAiBudgetCommand request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetBudgetAsync(cancellationToken);
        if (settings is null)
        {
            settings = AiBudgetSettings.CreateDefault();
            await _repository.AddBudgetAsync(settings, cancellationToken);
        }

        settings.Update(request.BudgetAmount, request.BudgetCurrency, request.UsdToAudRate, request.WarnPercent, request.StopAtLimit);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new AiBudgetSettingsDto(settings.BudgetAmount, settings.BudgetCurrency, settings.UsdToAudRate, settings.WarnPercent, settings.StopAtLimit);
    }
}
