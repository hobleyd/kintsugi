using System.Text.Json;

namespace Kintsugi.Application.GitHub;

/// <summary>
/// The manifest this server submits to GitHub to have an App created for it
/// (https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest).
/// </summary>
/// <remarks>
/// <para>
/// Permissions are exactly what the two token consumers need, and no more: <c>contents:write</c>
/// and <c>pull_requests:write</c> for publishing an approval, <c>metadata:read</c> because GitHub
/// requires it of every App. The read side needs nothing extra — <c>contents:read</c> is implied by
/// write, and <c>GitHubAppTokenProvider</c> narrows the token it hands the read-side consumers back
/// down to <c>contents:read</c>, so those consumers still never hold write access (see
/// <c>.claude/rules/script-approval-repo.md</c>).
/// </para>
/// <para>
/// No webhook. Nothing here listens for GitHub events, and an active hook would have GitHub
/// delivering every push on the approval repository to a server that ignores it. GitHub still
/// requires a URL in <c>hook_attributes</c> even when inactive, so the server's own address goes
/// there.
/// </para>
/// </remarks>
public static class GitHubAppManifest
{
    /// <summary>GitHub rejects an App name over this length, with a form error the manifest flow
    /// shows on GitHub's page rather than ours.</summary>
    public const int MaxNameLength = 34;

    /// <summary>The name proposed when the administrator gives none. App names are unique across
    /// all of GitHub, so the server's own host is folded in — two Kintsugi deployments in one
    /// organisation would otherwise collide on the second one's create.</summary>
    public static string DefaultName(string host)
    {
        var name = $"Kintsugi {host}";
        return name.Length <= MaxNameLength ? name : name[..MaxNameLength].TrimEnd('.', '-', ' ');
    }

    /// <param name="baseUrl">The address the browser reached this server on, scheme and host
    /// (and port, when not the default) — GitHub sends the browser back to it twice.</param>
    /// <param name="redirectUrl">Where GitHub sends the one-time conversion code.</param>
    /// <param name="setupUrl">Where GitHub sends the browser after the App is installed, carrying
    /// the installation id.</param>
    public static string Build(string name, string baseUrl, string redirectUrl, string setupUrl)
    {
        var manifest = new Dictionary<string, object>
        {
            ["name"] = name,
            ["url"] = baseUrl,
            ["description"] =
                "Kintsugi patch management: publishes human-approved upgrade scripts to the script-approval "
                + "repository, and reads agent builds and approvals.",
            ["hook_attributes"] = new Dictionary<string, object> { ["url"] = baseUrl, ["active"] = false },
            ["redirect_url"] = redirectUrl,
            ["setup_url"] = setupUrl,
            // Re-running setup when an installation's repository selection changes keeps the stored
            // installation id honest without anything else having to notice.
            ["setup_on_update"] = true,
            ["public"] = false,
            ["default_permissions"] = new Dictionary<string, string>
            {
                ["contents"] = "write",
                ["pull_requests"] = "write",
                ["metadata"] = "read",
            },
            ["default_events"] = Array.Empty<string>(),
        };

        return JsonSerializer.Serialize(manifest);
    }

    /// <summary>
    /// Where the browser posts the manifest. An organisation's App is created under that
    /// organisation's settings, which is what makes it the organisation's rather than the
    /// administrator's — and what keeps it working after that administrator leaves.
    /// </summary>
    public static string CreateUrl(string? organization, string state)
    {
        var escapedState = Uri.EscapeDataString(state);
        return string.IsNullOrWhiteSpace(organization)
            ? $"https://github.com/settings/apps/new?state={escapedState}"
            : $"https://github.com/organizations/{Uri.EscapeDataString(organization.Trim())}/settings/apps/new?state={escapedState}";
    }

    /// <summary>The App's installation page, where an administrator picks which repositories it
    /// may reach.</summary>
    public static string InstallUrl(string slug) =>
        $"https://github.com/apps/{Uri.EscapeDataString(slug)}/installations/new";
}
