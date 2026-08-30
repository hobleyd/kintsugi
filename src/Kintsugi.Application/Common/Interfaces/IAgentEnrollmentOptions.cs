namespace Kintsugi.Application.Common.Interfaces;

/// <summary>The one-time shared secret a brand-new agent presents to prove it's allowed to
/// enroll — see <c>EnrollAgentCommandHandler</c> and the <c>AGENT_ENROLLMENT_TOKEN</c> environment
/// variable it's configured from.</summary>
public interface IAgentEnrollmentOptions
{
    string? EnrollmentToken { get; }
}
