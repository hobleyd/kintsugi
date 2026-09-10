/// Enumerations mirrored by hand from the API's own.
///
/// Declaration order is load-bearing: several of these arrive as ordinals rather than names (see
/// `lib/core/network/json_reader.dart` for which, and why that asymmetry must not be "fixed"
/// server-side), so a member inserted anywhere but the end silently re-maps every value. The
/// C# definitions are in `src/Kintsugi.Domain/Enums/`.
library;

/// Mirrors `HostStatus`. Sent as an ordinal.
enum HostStatus {
  unknown,
  online,
  offline,
  decommissioned;

  /// The key the palette and the label lookup use, matching the CSS class names the old table
  /// built from `host.Status.ToString().ToLowerInvariant()`.
  String get key => name;

  String get label => switch (this) {
        HostStatus.unknown => 'Unknown',
        HostStatus.online => 'Online',
        HostStatus.offline => 'Offline',
        HostStatus.decommissioned => 'Decommissioned',
      };
}

/// Mirrors `UpgradePathStatus`. Sent as its name.
enum UpgradePathStatus { found, notFound, failed }

/// Mirrors `UpgradeMethod`. Sent as its name.
enum UpgradeMethod { unknown, directDownload, packageManagerCommand, manualSteps, script }

/// Mirrors `PatchFailureResolution`. Sent as an ordinal, so declaration order is load-bearing.
enum PatchFailureResolution {
  outstanding,
  patchSucceeded,
  dismissed,
  scriptRepaired;

  String get label => switch (this) {
        PatchFailureResolution.outstanding => 'Outstanding',
        PatchFailureResolution.patchSucceeded => 'Patched Since',
        PatchFailureResolution.dismissed => 'Dismissed',
        PatchFailureResolution.scriptRepaired => 'Repaired',
      };
}

/// Mirrors `AiProvider`. Sent as an ordinal.
enum AiProvider {
  anthropic,
  openAI,
  ollama,
  gooseCli,
  claudeAgentSdk;

  String get label => switch (this) {
        AiProvider.anthropic => 'Anthropic API (Claude)',
        AiProvider.openAI => 'OpenAI',
        AiProvider.ollama => 'Local LLM (Ollama)',
        AiProvider.gooseCli => 'Goose CLI',
        AiProvider.claudeAgentSdk => 'Claude Agent SDK (this server\'s Claude subscription)',
      };
}

/// Mirrors `AuthProvider`. Sent as an ordinal.
enum AuthProvider {
  googleWorkspace,
  microsoftEntra,
  genericOidc,
  clerk;

  String get label => switch (this) {
        AuthProvider.googleWorkspace => 'Google Workspace',
        AuthProvider.microsoftEntra => 'Microsoft Entra',
        AuthProvider.genericOidc => 'Generic OAuth2 / OIDC (Auth0, Okta, etc.)',
        AuthProvider.clerk => 'Clerk',
      };
}

/// Mirrors `AuditProvider`. Sent as an ordinal, like `AuthProvider`.
enum AuditProvider {
  datadog,
  grafanaLoki,
  googleCloudLogging,
  awsCloudWatch,
  azureMonitor,
  splunkHec,
  genericHttp;

  String get label => switch (this) {
        AuditProvider.datadog => 'Datadog',
        AuditProvider.grafanaLoki => 'Grafana Loki (Grafana Cloud or self-hosted)',
        AuditProvider.googleCloudLogging => 'Google Cloud Logging',
        AuditProvider.awsCloudWatch => 'AWS CloudWatch Logs',
        AuditProvider.azureMonitor => 'Azure Monitor (Log Analytics)',
        AuditProvider.splunkHec => 'Splunk (HTTP Event Collector)',
        AuditProvider.genericHttp => 'Other (generic HTTP endpoint)',
      };

  /// Whether the provider cannot be used without a secret. Mirrors
  /// `AuditSettings.RequiresSecret`: Loki and a generic endpoint may be unauthenticated; every
  /// other entry is a hosted service with a key. Decides whether the secret field says
  /// "required" or "optional", and nothing more — the server is what enforces it.
  bool get requiresSecret => this != AuditProvider.grafanaLoki && this != AuditProvider.genericHttp;
}

/// Mirrors `PatchingTimeUnit`. Sent as an ordinal — and this one's ordinal is read by all three
/// Rust agents (`policy.rs` parses `interval_unit` as a `u8`), so it is the clearest example of
/// why the wire format here is not ours to change.
enum PatchingTimeUnit {
  hours,
  days;

  String get label => switch (this) {
        PatchingTimeUnit.hours => 'Hours',
        PatchingTimeUnit.days => 'Days',
      };
}

/// Mirrors `AgentPackageImportOutcome`. Sent as an ordinal.
enum AgentPackageImportOutcome { imported, alreadyPublished, failed }

/// Mirrors `ScriptApprovalPublishOutcome`. Sent as its name, deliberately — see the comment on the
/// C# enum: this client reads the value straight out of the response to explain why signing did
/// not open a pull request, and an ordinal drifting as cases are reordered would be far worse than
/// an unrecognised name.
enum ScriptApprovalPublishOutcome {
  disabled,
  alreadyApproved,
  pullRequestAlreadyOpen,
  pullRequestOpened,
  failed,
  unknown,
}

/// Mirrors `RemoteControlConsent`. Sent as its name, like `UpgradePathStatus` — the C# enum carries
/// a converter. Safe there in a way it is not for the ordinal-encoded enums above, because no agent
/// reads this value at all: the agent is the one *reporting* it, as a name.
enum RemoteControlConsent {
  pending,
  granted,
  denied,
  timedOut,
  agentUnreachable,
  notRequired,
  unavailable;

  /// What the remote-control screen says happened. Phrased as the outcome rather than the state,
  /// because every one of these is something the administrator has to react to.
  String get label => switch (this) {
        RemoteControlConsent.pending => 'Waiting for the person at the keyboard to answer',
        RemoteControlConsent.granted => 'Allowed',
        RemoteControlConsent.denied => 'Refused by the person at the keyboard',
        RemoteControlConsent.timedOut => 'Nobody answered',
        RemoteControlConsent.agentUnreachable =>
          'This host is not reachable: its agent is not connected, which usually means it is asleep, '
              'switched off, or has nobody logged in',
        // A shell session, which asks nobody — see RemoteControlSessionKind.shell. Never shown as a
        // waiting state, because there is nothing to wait for.
        RemoteControlConsent.notRequired => 'Connected without asking the host',
        RemoteControlConsent.unavailable =>
          'This host cannot provide that kind of session right now: for a screen session that means '
              'nobody is logged in, so there is no desktop to share and nobody to ask',
      };
}

/// Mirrors `RemoteControlSessionKind`. A name on the wire, like [RemoteControlConsent] beside it.
enum RemoteControlSessionKind {
  screen,
  shell;

  /// What the request body carries. The server parses this case-insensitively, but it is written
  /// the way the C# member is spelled so a reader can match the two up.
  String get wireName => switch (this) {
        RemoteControlSessionKind.screen => 'Screen',
        RemoteControlSessionKind.shell => 'Shell',
      };

  String get label => switch (this) {
        RemoteControlSessionKind.screen => 'Screen control',
        RemoteControlSessionKind.shell => 'Terminal',
      };
}

/// Mirrors `CpeSubjectKind`. Ordinal on the wire, so declaration order matches the C# enum and new
/// members are appended, never inserted — see `.claude/rules/hand-mirrored-dtos.md`.
enum CpeSubjectKind {
  application,
  operatingSystem;

  String get label => switch (this) {
        CpeSubjectKind.application => 'Application',
        CpeSubjectKind.operatingSystem => 'Operating system',
      };
}

/// Mirrors `CpeMappingStatus`. Ordinal on the wire; append only.
enum CpeMappingStatus {
  unmapped,
  suggested,
  confirmed,
  notApplicable;

  String get label => switch (this) {
        CpeMappingStatus.unmapped => 'Not mapped',
        CpeMappingStatus.suggested => 'Awaiting review',
        CpeMappingStatus.confirmed => 'Mapped',
        CpeMappingStatus.notApplicable => 'Not applicable',
      };

  /// What the mapping screen says this state means for coverage. Spelled out rather than left to
  /// a colour, because "nothing has been assessed for this" is the part of the picture a
  /// vulnerability screen most easily hides.
  String get description => switch (this) {
        CpeMappingStatus.unmapped =>
          'Nothing has been assessed for this — no CPE has been confirmed, so NVD has never been asked about it.',
        CpeMappingStatus.suggested =>
          'A CPE has been proposed and confirmed to exist in NVD’s dictionary, but nothing is assessed '
              'until somebody accepts it.',
        CpeMappingStatus.confirmed => 'Assessed against NVD at every version the fleet has installed.',
        CpeMappingStatus.notApplicable =>
          'Deliberately not assessed. Excluded from the not-assessed count, because this is a decision '
              'rather than a gap.',
      };
}

/// Mirrors `CpeSuggestionSource`. Ordinal on the wire; append only.
enum CpeSuggestionSource {
  none,
  ai,
  dictionary,
  manual;

  String get label => switch (this) {
        CpeSuggestionSource.none => '',
        CpeSuggestionSource.ai => 'Suggested by AI',
        CpeSuggestionSource.dictionary => 'From NVD’s dictionary',
        CpeSuggestionSource.manual => 'Entered by hand',
      };
}


/// Mirrors `VulnerabilitySubjectKind` — what a finding was matched against.
///
/// Deliberately not [CpeSubjectKind], even though the first two members line up with it (and
/// their ordinals match on purpose). That one is about what a CPE mapping maps; a distribution
/// package has no CPE mapping at all — it is matched against its distribution's own advisories,
/// keyed by source-package name, with nothing for a human to confirm. Ordinal on the wire;
/// append only.
enum VulnerabilitySubjectKind {
  application,
  operatingSystem,
  operatingSystemPackage;

  String get label => switch (this) {
        VulnerabilitySubjectKind.application => 'Application',
        VulnerabilitySubjectKind.operatingSystem => 'Operating system',
        VulnerabilitySubjectKind.operatingSystemPackage => 'OS package',
      };

  /// Where the answer came from, shown beside a finding so a reader knows which claim they are
  /// looking at. The distinction is not cosmetic: a package's answer accounts for the
  /// distribution's backported fixes, and an application's does not have to.
  String get sourceLabel => switch (this) {
        VulnerabilitySubjectKind.application ||
        VulnerabilitySubjectKind.operatingSystem =>
          'Matched by version range against the National Vulnerability Database.',
        VulnerabilitySubjectKind.operatingSystemPackage =>
          'Matched against this distribution’s own security advisories, so a backported fix counts '
              'as fixed.',
      };
}
