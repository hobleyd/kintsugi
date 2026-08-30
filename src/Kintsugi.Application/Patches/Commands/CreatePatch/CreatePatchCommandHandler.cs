using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Patches.Commands.CreatePatch;

public class CreatePatchCommandHandler : IRequestHandler<CreatePatchCommand, PatchDto>
{
    private readonly IPatchRepository _patchRepository;
    private readonly IUnitOfWork _unitOfWork;

    public CreatePatchCommandHandler(IPatchRepository patchRepository, IUnitOfWork unitOfWork)
    {
        _patchRepository = patchRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<PatchDto> Handle(CreatePatchCommand request, CancellationToken cancellationToken)
    {
        var patch = new Patch(request.Name, request.Vendor, request.Version, request.Severity, request.ReleasedUtc, request.Description);

        await _patchRepository.AddAsync(patch, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return PatchDto.FromEntity(patch);
    }
}
