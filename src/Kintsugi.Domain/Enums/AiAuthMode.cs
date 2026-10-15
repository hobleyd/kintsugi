namespace Kintsugi.Domain.Enums;

/// <summary>How a connection proves who it is. Persisted by name.</summary>
public enum AiAuthMode
{
    /// <summary>A key stored with the connection, sent the way its protocol expects.</summary>
    ApiKey,

    /// <summary>Nothing — a local Ollama, or a self-hosted server on a trusted network.</summary>
    None,

    /// <summary>
    /// A Google Cloud access token from the metadata server: the identity the server runs as. On
    /// GKE with Workload Identity that is the pod's Google service account, so <b>no key is stored
    /// anywhere</b> — the token is minted per hour by the platform. Used for Vertex AI.
    /// </summary>
    GoogleCloud,
}
