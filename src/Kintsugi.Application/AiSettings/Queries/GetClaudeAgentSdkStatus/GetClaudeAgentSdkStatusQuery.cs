using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.AiSettings.Queries.GetClaudeAgentSdkStatus;

/// <summary>Takes no parameters, unlike <c>GetGooseCliStatusQuery</c>: what this probe needs is
/// the OAuth token, and the token is never sent to the browser, so it is read from what is stored
/// rather than from what is on screen. That also makes the answer describe the configuration the
/// server will actually run with.</summary>
public record GetClaudeAgentSdkStatusQuery : IRequest<ClaudeAgentSdkStatus>;
