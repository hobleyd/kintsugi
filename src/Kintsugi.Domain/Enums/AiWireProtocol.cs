namespace Kintsugi.Domain.Enums;

/// <summary>
/// The wire shape a connection speaks. A handful of these covers hundreds of providers: models.dev
/// lists 226, and all but a few speak one of these — so adding a provider is a configuration row,
/// not code. Adding a genuinely new shape is one adapter class in
/// <c>Kintsugi.Infrastructure.Ai.Engine</c>.
/// </summary>
/// <remarks>
/// Persisted as its <em>name</em> and sent to the admin UI as one, so order does not matter here —
/// but append rather than insert anyway, for the habit's sake. Mirrored in
/// <c>web/lib/domain/entities/enums.dart</c>.
/// </remarks>
public enum AiWireProtocol
{
    /// <summary><c>POST {base}/chat/completions</c>: OpenAI's original shape, which nearly every
    /// third-party and self-hosted server copies — LiteLLM, vLLM, LM Studio, OpenRouter, Groq,
    /// Mistral, Vertex AI's OpenAI-compatible endpoint, Azure's <c>/openai/v1</c>.</summary>
    OpenAiChatCompletions,

    /// <summary><c>POST {base}/responses</c>: OpenAI's newer shape, and the one its hosted web
    /// search tool lives on.</summary>
    OpenAiResponses,

    /// <summary>Anthropic's Messages API — direct, through an Anthropic-compatible host, or as Claude
    /// on Vertex AI (<c>:rawPredict</c>) when the connection authenticates with Google Cloud.</summary>
    Anthropic,

    /// <summary>Gemini's <c>generateContent</c> — Google AI Studio with an API key, or Vertex AI
    /// when the connection authenticates with Google Cloud.</summary>
    Google,

    /// <summary>Ollama's own <c>/api/chat</c>.</summary>
    Ollama,
}
