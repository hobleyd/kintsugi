using FluentValidation;
using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.AiSettings.Commands.SaveAiConnection;

/// <summary>Creates a connection (<see cref="Id"/> null) or updates one. A blank
/// <see cref="ApiKey"/> on an update keeps the stored key.</summary>
public record SaveAiConnectionCommand(
    Guid? Id,
    string Name,
    string? CatalogProviderId,
    AiWireProtocol Protocol,
    string? BaseUrl,
    AiAuthMode AuthMode,
    string? ApiKey,
    string? GoogleCloudProject,
    string? GoogleCloudLocation,
    bool UseHostedWebSearch) : IRequest<AiConnectionDto>;

public class SaveAiConnectionCommandValidator : AbstractValidator<SaveAiConnectionCommand>
{
    public SaveAiConnectionCommandValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(100);
        RuleFor(c => c.BaseUrl).MaximumLength(512);
        RuleFor(c => c.ApiKey).MaximumLength(1024);
        RuleFor(c => c.CatalogProviderId).MaximumLength(100);
        // A project id and a location are interpolated into Vertex URL paths.
        RuleFor(c => c.GoogleCloudProject).Matches("^[a-z][a-z0-9-]{4,61}[a-z0-9]$")
            .When(c => !string.IsNullOrWhiteSpace(c.GoogleCloudProject))
            .WithMessage("That is not a Google Cloud project id.");
        RuleFor(c => c.GoogleCloudLocation).Matches("^[a-z0-9-]{2,40}$")
            .When(c => !string.IsNullOrWhiteSpace(c.GoogleCloudLocation))
            .WithMessage("That is not a Google Cloud location — e.g. australia-southeast1, us-east5 or global.");
    }
}

public class SaveAiConnectionCommandHandler : IRequestHandler<SaveAiConnectionCommand, AiConnectionDto>
{
    private readonly IAiConnectionRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public SaveAiConnectionCommandHandler(IAiConnectionRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<AiConnectionDto> Handle(SaveAiConnectionCommand request, CancellationToken cancellationToken)
    {
        AiConnection connection;
        if (request.Id is { } id)
        {
            connection = await _repository.GetAsync(id, cancellationToken)
                ?? throw new NotFoundException($"AI connection {id} was not found.");
            connection.Update(request.Name, request.CatalogProviderId, request.Protocol, request.BaseUrl, request.AuthMode,
                request.ApiKey, request.GoogleCloudProject, request.GoogleCloudLocation, request.UseHostedWebSearch);
        }
        else
        {
            connection = AiConnection.Create(request.Name, request.CatalogProviderId, request.Protocol, request.BaseUrl, request.AuthMode,
                request.ApiKey, request.GoogleCloudProject, request.GoogleCloudLocation, request.UseHostedWebSearch);
            await _repository.AddAsync(connection, cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return AiConnectionDto.FromEntity(connection);
    }
}
