using Kintsugi.Application.AiSettings;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Infrastructure.Ai.Engine;

/// <summary>
/// Speaks one <see cref="AiWireProtocol"/>. Every provider reached through it is configuration:
/// where to send (the connection's base URL, or Vertex AI's, built from project and location) and
/// how to authenticate.
/// </summary>
public interface IAiProtocolAdapter
{
    AiWireProtocol Protocol { get; }

    /// <summary>One round trip. Throws <c>ExternalServiceException</c> for anything but a usable
    /// answer, carrying the provider's own error body — it is what the operator reads.</summary>
    Task<AiChatResult> SendAsync(AiConnectionSettings connection, AiChatRequest request, CancellationToken cancellationToken);
}
