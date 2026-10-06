namespace Kintsugi.Application.GitHub;

/// <summary>
/// What the Settings page needs to start the manifest flow: GitHub's create-App page and the
/// manifest to post to it.
/// </summary>
/// <remarks>
/// The browser has to make that POST itself — GitHub shows the administrator the App it is about to
/// create and asks them to confirm, which no server-side request could do on their behalf. So the
/// page builds a form from these two values and submits it.
/// </remarks>
/// <param name="CreateUrl">GitHub's create page, carrying this server's signed, expiring
/// <c>state</c>.</param>
/// <param name="Manifest">The manifest JSON, posted as the form's <c>manifest</c> field.</param>
public record GitHubAppManifestDto(string CreateUrl, string Manifest);
