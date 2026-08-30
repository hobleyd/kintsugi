namespace Kintsugi.Application.Common.Interfaces;

public interface IOllamaModelsClient
{
    /// <summary>Lists the model names installed on the Ollama instance at <paramref name="baseUrl"/>.</summary>
    Task<IReadOnlyList<string>> GetAvailableModelsAsync(string baseUrl, CancellationToken cancellationToken);
}
