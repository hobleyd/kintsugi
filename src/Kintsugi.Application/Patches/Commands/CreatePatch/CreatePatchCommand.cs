using MediatR;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Patches.Commands.CreatePatch;

public record CreatePatchCommand(
    string Name,
    string Vendor,
    string Version,
    PatchSeverity Severity,
    DateTimeOffset ReleasedUtc,
    string? Description) : IRequest<PatchDto>;
