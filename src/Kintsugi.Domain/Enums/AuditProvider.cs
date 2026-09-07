namespace Kintsugi.Domain.Enums;

/// <summary>
/// The logging platform audit events are shipped to. Which fields each one needs is
/// <see cref="Entities.AuditSettings"/>; how each is reached is described on the Auditing settings
/// screen (<c>web/lib/presentation/settings/auditing_screen.dart</c>).
/// </summary>
/// <remarks>
/// No JSON converter, like <see cref="AuthProvider"/> and <see cref="AiProvider"/>, so this crosses
/// the wire as an ordinal — which makes declaration order load-bearing on both ends. Append new
/// members; never insert or reorder. The mirror is <c>AuditProvider</c> in
/// <c>web/lib/domain/entities/enums.dart</c>.
/// </remarks>
public enum AuditProvider
{
    Datadog,
    GrafanaLoki,
    GoogleCloudLogging,
    AwsCloudWatch,
    AzureMonitor,
    SplunkHec,
    GenericHttp
}
