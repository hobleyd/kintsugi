using MediatR;

namespace Kintsugi.Application.ForcedPatchRuns.Commands.RequestForcedPatchRuns;

/// <summary>
/// Tells a named set of hosts to run one application's upgrade script at their next opportunity,
/// rather than waiting for their own patching schedule. Raised by the Applications screen's "Patch
/// now" action; browser-driven, so it reaches the server through <c>/api/admin/applications/</c>
/// behind <c>[RequireAdminSession]</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The browser names the hosts; the server does not re-derive them.</strong> The screen's
/// filters are client-side — everything they need is already in the overview response — so the set
/// the operator is looking at exists only in the browser. Sending the criteria instead would put
/// the same filter semantics in two places free to drift, and would make the stored record say "all
/// Windows hosts" rather than naming the machines that were actually told. So the client resolves
/// its filter to a host list and this command carries it.
/// </para>
/// <para>
/// <strong>Hosts arrive as names, not serial numbers.</strong> A serial number is a host's identity
/// on the wire, but the Applications screen has never had one: its rows carry host <em>names</em>,
/// which is what the filters match on and what the operator picked from. Translating a name to the
/// server's own key is a lookup, not filter logic, so doing it here keeps the criteria in one place
/// without putting serial numbers into a fleet-wide listing that has no other use for them. A name
/// that resolves to nothing comes back in <c>NotRequested</c> rather than failing the whole call —
/// see <see cref="RequestForcedPatchRunsResult"/>.
/// </para>
/// </remarks>
public record RequestForcedPatchRunsCommand(
    string ApplicationName,
    string Platform,
    IReadOnlyList<string> HostNames) : IRequest<RequestForcedPatchRunsResult>;
