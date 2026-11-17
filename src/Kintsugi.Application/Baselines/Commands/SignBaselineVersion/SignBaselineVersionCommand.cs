using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ScriptApproval;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.Baselines.Commands.SignBaselineVersion;

/// <summary>
/// Signs one baseline version with the artifact-signing key after a person has reviewed it. Bound to the
/// bytes the reviewer saw, exactly as <c>SignUpgradePathScriptCommandHandler</c> is: the request carries
/// the SHA-256 of the document the screen displayed, and the handler refuses unless the stored document
/// still hashes to it.
/// </summary>
/// <param name="ExpectedDocumentSha256">Lowercase hex SHA-256 of the document text the reviewer was
/// shown (<c>baselines_screen.dart</c> hashes the string it received). Required, with no fallback.</param>
/// <param name="SignedBy">The reviewer, from the session (<c>AdminIdentity.SignerOf</c>).</param>
public record SignBaselineVersionCommand(Guid VersionId, string ExpectedDocumentSha256, string? SignedBy = null)
    : IRequest<BaselineVersionDto>;
