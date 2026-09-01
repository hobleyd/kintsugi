using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.PatchingPolicy;
using Kintsugi.Application.PatchingPolicy.Queries.GetPatchingPolicySettings;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The fleet-wide patching policy — how often patching runs, and how long/how many times a
/// required restart or reboot can be deferred. Read by the kintsugi-agent (in a future step) to
/// decide when to check for and apply patches, and how to handle a disruptive one, rather than the
/// server pushing patches on a schedule itself.
/// </summary>
[ApiController]
[Route("api/patching-policy")]
[Produces("application/json")]
public class PatchingPolicyController : ControllerBase
{
    private readonly ISender _sender;

    public PatchingPolicyController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Gets the current patching policy — sensible defaults if none has been saved yet.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PatchingPolicySettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PatchingPolicySettingsDto>> Get(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetPatchingPolicySettingsQuery(), cancellationToken));

}

// There is deliberately no PUT here, and it is worth saying why so one doesn't come back.
//
// This path is inside nginx's exact-match agent regex, so a valid fleet client certificate is
// required to reach it — which sounds like protection and is the opposite. The GET is right to be
// there: it is the route all three agents poll (see each agent's policy.rs). A PUT on the same path
// inherits the same admission rule, and it carried no [RequireAgentIdentity] and no admin gate, so
// **any enrolled agent could rewrite the fleet-wide patching policy** — deferral limits, maintenance
// windows, whether patching is enabled at all — for every other host. Meanwhile a browser could not
// reach it, because a browser has no agent certificate.
//
// Nothing legitimate used it: the Settings page dispatches UpdatePatchingPolicySettingsCommand
// through ISender like every other Razor page, and no agent ever issued anything but a GET.
//
// A write route for this belongs on a path *outside* that regex, carrying [RequireAdminSession] —
// or, better, stays where it already is: the page handler.
