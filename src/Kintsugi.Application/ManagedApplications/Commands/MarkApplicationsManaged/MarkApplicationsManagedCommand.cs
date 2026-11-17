using MediatR;

namespace Kintsugi.Application.ManagedApplications.Commands.MarkApplicationsManaged;

/// <summary>
/// Says that <paramref name="Owner"/> patches each of <paramref name="Names"/>. An application that is
/// already marked is moved to the new owner; an owner not on the list yet is added to it.
/// </summary>
/// <remarks>Marking an application stops Kintsugi researching it and patching it, on every host.</remarks>
public record MarkApplicationsManagedCommand(IReadOnlyList<string> Names, string Owner) : IRequest<ManagedApplicationsDto>;
