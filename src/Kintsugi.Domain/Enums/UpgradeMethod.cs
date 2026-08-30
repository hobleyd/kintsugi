using System.Text.Json.Serialization;
using Kintsugi.Domain.Common;

namespace Kintsugi.Domain.Enums;

/// <summary>How an installed application should be brought up to its latest version.</summary>
[JsonConverter(typeof(LenientEnumConverter<UpgradeMethod>))]
public enum UpgradeMethod
{
    /// <summary>No reliable upgrade information has been found yet.</summary>
    Unknown,

    /// <summary>Downloading and installing <c>DownloadUrl</c> is sufficient.</summary>
    DirectDownload,

    /// <summary>The application is managed by a package manager (e.g. Homebrew); run <c>Command</c>.</summary>
    PackageManagerCommand,

    /// <summary>The upgrade needs more than a single download — see <c>Instructions</c>.</summary>
    ManualSteps,

    /// <summary>An AI-generated per-application script (<c>Script</c>) handles both checking for
    /// and installing updates — <c>script.sh --update-version</c> / <c>--update</c>.</summary>
    Script
}
