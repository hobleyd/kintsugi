using MediatR;

namespace Kintsugi.Application.ScriptApproval.Commands.AdoptApprovedScript;

/// <summary>
/// Takes an approved script this server does not have and puts it on one local upgrade path, signed
/// with this server's own key so its agents will run it.
/// </summary>
/// <remarks>
/// A deliberate per-row action rather than something "Refresh scripts" does for you. Blessing content
/// that already exists locally is safe to automate — nothing new arrives. Adoption brings in content
/// from a repository, and the configured trust root is that repository's default branch alone, so an
/// automatic version would make a merge there sufficient to place new executable content on every
/// server that refreshed. Requiring a person to press it, with the signer's fingerprint on screen
/// next to it, is what keeps a merge from being the last human decision in the chain.
/// </remarks>
/// <param name="Sha256">Which approved content to take, named explicitly rather than resolved as
/// "the newest for this application" — the page showed a specific entry, by a specific signer, and
/// this must adopt that one and not whatever has arrived since.</param>
public record AdoptApprovedScriptCommand(
    string ApplicationName,
    string Platform,
    string Sha256,
    string SignerFingerprint) : IRequest<AdoptApprovedScriptResultDto>;

public record AdoptApprovedScriptResultDto(
    string ApplicationName,
    string Platform,
    string Sha256,
    string SignerFingerprint);
