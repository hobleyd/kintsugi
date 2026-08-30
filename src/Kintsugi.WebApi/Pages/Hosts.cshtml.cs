using MediatR;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Kintsugi.Application.Hosts;
using Kintsugi.Application.Hosts.Queries.GetHosts;

namespace Kintsugi.WebApi.Pages;

public class HostsModel : PageModel
{
    private readonly ISender _sender;

    public HostsModel(ISender sender)
    {
        _sender = sender;
    }

    public IReadOnlyList<HostDto> Hosts { get; private set; } = Array.Empty<HostDto>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Hosts = await _sender.Send(new GetHostsQuery(), cancellationToken);
    }
}
