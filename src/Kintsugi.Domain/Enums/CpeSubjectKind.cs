namespace Kintsugi.Domain.Enums;

/// <summary>
/// What an <see cref="Entities.CpeMapping"/> maps — which decides both the CPE part letter
/// (<c>a</c> for an application, <c>o</c> for an operating system) and where the versions to
/// assess come from.
/// </summary>
/// <remarks>
/// Ordinal on the wire, like <see cref="CpeMappingStatus"/>; append only. The mirror is
/// <c>CpeSubjectKind</c> in <c>web/lib/domain/entities/enums.dart</c>.
/// </remarks>
public enum CpeSubjectKind
{
    /// <summary>An installed application, keyed on the name the agent reports it under. Versions
    /// come from the distinct <c>InstalledApplication.Version</c> values across live hosts.</summary>
    Application,

    /// <summary>A host operating system, keyed on the product token derived from
    /// <c>Host.OperatingSystem</c> (see <c>OperatingSystemSubject</c>). Versions come from the
    /// distinct OS versions across live hosts.</summary>
    OperatingSystem
}
