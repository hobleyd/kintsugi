using MediatR;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Kintsugi.Application.AgentPackages;
using Kintsugi.Application.AgentPackages.Queries.GetAgentPackages;

namespace Kintsugi.WebApi.Pages;

public class ClientsModel : PageModel
{
    private readonly ISender _sender;

    public ClientsModel(ISender sender)
    {
        _sender = sender;
    }

    public IReadOnlyList<AgentPackageDto> Packages { get; private set; } = Array.Empty<AgentPackageDto>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Packages = await _sender.Send(new GetAgentPackagesQuery(), cancellationToken);
    }
}
