using MediatR;

namespace Kintsugi.Application.ManagedApplications.Commands.UnmarkApplicationManaged;

/// <summary>Gives an application back to Kintsugi. Not being marked already is not an error: the
/// list is as the caller wanted it.</summary>
public record UnmarkApplicationManagedCommand(string ApplicationName) : IRequest<ManagedApplicationsDto>;
