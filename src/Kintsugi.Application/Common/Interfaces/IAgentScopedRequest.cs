namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// Implemented by a command/query whose <see cref="SerialNumber"/> must match the identity nginx
/// already proved via mutual TLS (forwarded as the <c>X-Agent-Cert-Cn</c> header — see
/// <c>RequireAgentIdentityAttribute</c>). This is the second half of "the two systems are the
/// right ones": mTLS proves *an* enrolled agent is calling, this proves it's calling on behalf of
/// the host it was actually enrolled as, not spoofing another host's serial number in the request
/// body while presenting its own valid certificate.
/// </summary>
public interface IAgentScopedRequest
{
    string SerialNumber { get; }
}
