namespace Kintsugi.Application.Common.Interfaces;

/// <summary>What GitHub hands back when a manifest is converted into an App.</summary>
/// <param name="AppId">The App's numeric id — the <c>iss</c> of every JWT it signs.</param>
/// <param name="Slug">The App's URL slug, which its installation page and its bot login are
/// built from.</param>
/// <param name="Owner">The login of the account that owns the App.</param>
/// <param name="PrivateKeyPem">The App's private key. GitHub returns it exactly once, here.</param>
public record GitHubAppCredentials(long AppId, string Slug, string Owner, string PrivateKeyPem);

/// <summary>
/// The GitHub calls the App flow needs that are not themselves token minting: turning the
/// one-time manifest code into credentials, and confirming that an installation id really belongs
/// to the stored App.
/// </summary>
/// <remarks>
/// Minting installation tokens is <c>GitHubAppTokenProvider</c>'s job, not this client's, because
/// that cache has to be a singleton and a typed client is transient — the same split
/// <c>VantaAccessTokenProvider</c> makes from <c>VantaSyncClient</c>.
/// </remarks>
public interface IGitHubAppClient
{
    /// <summary>
    /// Exchanges the one-time <paramref name="code"/> GitHub appended to the manifest flow's
    /// redirect for the new App's credentials. The code expires an hour after issue and is
    /// single-use, so a failure here cannot simply be retried by reloading the callback.
    /// </summary>
    Task<GitHubAppCredentials> ConvertManifestAsync(string code, CancellationToken cancellationToken);

    /// <summary>
    /// The login of the account <paramref name="installationId"/> is installed on, asked
    /// <em>as the App</em>. GitHub answers only for the App's own installations, so a successful
    /// answer is the proof that an id arriving on a redirect's query string is ours to use.
    /// </summary>
    Task<string> GetInstallationAccountAsync(
        long appId, string privateKeyPem, long installationId, CancellationToken cancellationToken);
}
