namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// Which interpreter an upgrade script is written for. Both run on the (Linux) API server for a
/// script's <c>--update-version</c> mode — bash natively, PowerShell via <c>pwsh</c>, which is why
/// the runtime image installs it (see <c>src/Kintsugi.WebApi/Dockerfile</c>) — and on the managed
/// host itself for <c>--update</c> mode.
/// </summary>
public enum ScriptLanguage
{
    Bash,
    PowerShell
}

public static class ScriptLanguages
{
    /// <summary>
    /// The language a script stored under <paramref name="platform"/> is written in. Windows hosts
    /// and the Windows package managers get PowerShell; everything else gets bash. Deliberately
    /// derived from the bucket rather than sniffed from the script text: the bucket is what decides
    /// which prompt generated it, which validator checks it, and which interpreter runs it, so all
    /// four stay in agreement by construction.
    /// </summary>
    public static ScriptLanguage For(string platform)
    {
        var packageManager = PlatformBucket.PackageManagerNameFrom(platform);
        if (packageManager is not null)
        {
            return PackageManagerCatalog.TryGet(packageManager, out var recognized)
                ? recognized.Language
                // An unrecognized manager never gets a script written for it at all (see
                // ResearchApplicationUpgradePathCommandHandler), so this only decides how a
                // hand-written row would be treated — bash, matching every other unknown bucket.
                : ScriptLanguage.Bash;
        }

        return platform.Equals(PlatformBucket.Windows, StringComparison.OrdinalIgnoreCase)
            ? ScriptLanguage.PowerShell
            : ScriptLanguage.Bash;
    }

    /// <summary>The executable that runs a script in this language, as found on the API server's
    /// own PATH.</summary>
    public static string Interpreter(this ScriptLanguage language) =>
        language == ScriptLanguage.PowerShell ? "pwsh" : "bash";

    /// <summary>The file extension a temp copy of the script needs before its interpreter will run
    /// it — <c>pwsh -File</c> refuses anything that isn't <c>.ps1</c>.</summary>
    public static string FileExtension(this ScriptLanguage language) =>
        language == ScriptLanguage.PowerShell ? ".ps1" : ".sh";
}
