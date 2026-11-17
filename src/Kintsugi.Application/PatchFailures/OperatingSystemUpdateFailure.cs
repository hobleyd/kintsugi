namespace Kintsugi.Application.PatchFailures;

/// <summary>
/// Recognises a failed <em>operating system</em> update among the Failed Updates rows and says, in
/// plain words, why it failed and what to do about it.
/// </summary>
/// <remarks>
/// <para>
/// The three agents file a failed OS update through the same route as a failed application, under
/// the application name <c>macOS</c>, <c>Windows</c> or <c>Linux</c> (<c>OS_FAILURE_APPLICATION_NAME</c>
/// in each agent's <c>os_update.rs</c>). Nothing but that name tells the two apart, so the names live
/// here once and the screen asks the server rather than guessing.
/// </para>
/// <para>
/// The explanation is read from the agent's captured output, which is whatever Windows Update, macOS
/// <c>softwareupdate</c> or the distribution's package manager printed. It is a best effort: the
/// patterns below are the failures actually seen in the field, and anything else gets an honest
/// "it failed, read the output" rather than a guess presented as a cause. Adding a pattern is adding
/// a row; nothing else has to change.
/// </para>
/// </remarks>
public static class OperatingSystemUpdateFailure
{
    public const string MacOs = "macOS";
    public const string Windows = "Windows";
    public const string Linux = "Linux";

    /// <summary>What the screen calls each one, since "Windows" alone in an Application column does
    /// not say it is the operating system rather than an application of that name.</summary>
    public static string? DisplayName(string applicationName) => applicationName switch
    {
        MacOs => "macOS update",
        Windows => "Windows Update",
        Linux => "Linux system update",
        _ => null
    };

    public static bool IsOperatingSystem(string applicationName) => DisplayName(applicationName) is not null;

    /// <summary>A short cause, a sentence saying why it happens, and what to do.</summary>
    public sealed record Cause(string Title, string Explanation, string SuggestedFix);

    /// <summary>
    /// The cause of this failure, or null when <paramref name="applicationName"/> is an ordinary
    /// application. An operating-system failure whose output matches nothing known still gets a cause,
    /// an honest generic one.
    /// </summary>
    public static Cause? Explain(string applicationName, string details)
    {
        if (!IsOperatingSystem(applicationName))
        {
            return null;
        }

        var text = details ?? string.Empty;

        foreach (var rule in Rules)
        {
            if (rule.AppliesTo is not null && rule.AppliesTo != applicationName)
            {
                continue;
            }

            if (rule.Markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return rule.Cause;
            }
        }

        return new Cause(
            "The update ran and failed",
            "The host started installing its operating system updates and the installer reported an error. "
            + "Kintsugi does not recognise this particular message.",
            "Read the output below for the exact error. If it keeps failing, the host's own update log has more.");
    }

    private sealed record Rule(string? AppliesTo, string[] Markers, Cause Cause);

    // Order matters: the first rule that matches wins, so the more specific ones come first.
    private static readonly Rule[] Rules =
    [
        // Windows
        new(Windows, ["0x80070070", "not enough space", "not enough storage"],
            new Cause("Not enough disk space",
                "Windows needs free space on the system drive to download and install the update, and there was not enough.",
                "Free up space on C: (temporary files, the Windows Update download cache), then patch again.")),
        new(Windows, ["exit code: 3010", "0x80240fff", "restart is required", "restart required", "reboot required"],
            new Cause("A restart is needed first",
                "An earlier update is waiting for the host to restart before anything else can install.",
                "Restart the host, then patch again. A forced restart with its 5-minute warning clears it.")),
        new(Windows, ["0x80072ee2", "0x80072efd", "0x80072ee7", "0x80244022", "0x8024402c", "0x80240438"],
            new Cause("Windows Update could not be reached",
                "The host could not connect to Microsoft's update servers, usually because of the network, a proxy or DNS.",
                "Check the host's internet access, proxy and DNS, then patch again.")),
        new(Windows, ["0x80073712", "0x800f0831", "0x800f081f", "0x80070bc9"],
            new Cause("Windows' update files are damaged",
                "Part of Windows' own update store is corrupt or missing, so the update cannot be applied.",
                "Run the Windows repair (DISM and SFC) on the host, or reset the Windows Update components, then patch again.")),
        // Seen on a real host: a Microsoft Store app's update (myHP) failing with this code on and off
        // for days, then installing by itself on a later try.
        new(Windows, ["0x80073d02"],
            new Cause("An app was open and blocked the update",
                "The update is for an app that was running, so Windows could not replace its files. This usually clears on a later try.",
                "Close the app, or restart the host, then patch again.")),
        new(Windows, ["0x80070005", "access is denied"],
            new Cause("Access was denied",
                "The installer was refused permission, often because security software or a policy blocked it.",
                "Check endpoint protection and group policy on the host for something blocking Windows Update.")),
        new(Windows, ["0x80070643"],
            new Cause("The installer reported a fatal error",
                "Windows Update started the installation and it failed part-way. This code covers several causes, a "
                + "broken previous install and a stopped Windows Installer service among them.",
                "Restart the Windows Update and Windows Installer services, restart the host, then patch again.")),
        new(Windows, ["failed to run powershell", "failed to run \"powershell"],
            new Cause("PowerShell could not be started",
                "The agent drives Windows Update through PowerShell, and it could not be launched.",
                "Check that Windows PowerShell is present and not blocked by policy on the host.")),

        // macOS
        new(MacOs, ["not authorized", "authorization", "secure token", "requires authentication"],
            new Cause("macOS would not authorise the install",
                "macOS refuses an unattended update unless the host holds the right credentials, which usually means no "
                + "user with a secure token has been available.",
                "Make sure a user with a secure token has logged in on the host, then patch again.")),
        new(MacOs, ["no space", "not enough free", "insufficient space", "disk space"],
            new Cause("Not enough disk space",
                "macOS needs free space to download and stage the update, and there was not enough.",
                "Free up disk space on the host, then patch again.")),
        new(MacOs, ["could not download", "download failed", "failed to download", "network connection was lost", "could not connect"],
            new Cause("The update could not be downloaded",
                "The host could not finish downloading the update from Apple.",
                "Check the host's internet access, then patch again.")),

        // Linux
        new(Linux, ["could not get lock", "unable to acquire the dpkg", "lock-frontend", "another process", "is held by process"],
            new Cause("Another package manager is running",
                "The distribution's package database is locked because another update or install is in progress.",
                "Wait for the other process to finish, or check for a stuck apt, dpkg or dnf process, then patch again.")),
        new(Linux, ["dpkg was interrupted", "dpkg --configure -a"],
            new Cause("An earlier install was interrupted",
                "dpkg was stopped half-way through a previous run and has to be repaired before it will install anything.",
                "Run 'dpkg --configure -a' on the host, then patch again.")),
        new(Linux, ["no space left", "not enough free space", "insufficient disk space"],
            new Cause("Not enough disk space",
                "There was not enough free space to download and unpack the updates.",
                "Free up space (apt-get clean, old kernels, logs), then patch again.")),
        new(Linux, ["could not resolve", "temporary failure resolving", "failed to fetch", "network is unreachable", "connection timed out", "cannot find a valid baseurl"],
            new Cause("The package repositories could not be reached",
                "The host could not download from its configured repositories, because of the network, DNS or a repository that has moved.",
                "Check the host's internet access and its repository list, then patch again.")),
        new(Linux, ["held broken packages", "unmet dependencies", "conflicting requests", "problem: "],
            new Cause("Package dependencies cannot be resolved",
                "Some installed packages conflict with the updates, so the package manager refused to continue.",
                "Run the package manager by hand on the host to see which package is blocking, and fix or remove it.")),
        new(Linux, ["no_pubkey", "gpg error", "signature verification failed", "gpg check failed"],
            new Cause("A repository's signing key is missing or expired",
                "The package manager does not trust one of the repositories, so it stopped.",
                "Update the repository's signing key on the host, then patch again.")),
        new(Linux, ["no supported distribution package manager"],
            new Cause("This distribution is not supported",
                "The agent could not find a package manager it knows how to drive on this host.",
                "Kintsugi supports apt, dnf, yum, zypper, pacman and apk. This host will have to be updated another way.")),
    ];
}
