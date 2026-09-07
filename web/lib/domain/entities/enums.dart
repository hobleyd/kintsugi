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
