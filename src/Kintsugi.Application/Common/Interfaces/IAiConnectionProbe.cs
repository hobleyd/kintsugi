using Kintsugi.Application.AiSettings;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>Sends one tiny prompt down a connection — the settings page's Test button.</summary>
public interface IAiConnectionProbe
{
    Task<AiConnectionTestResultDto> ProbeAsync(AiConnectionSettings connection, string model, CancellationToken cancellationToken);
}
