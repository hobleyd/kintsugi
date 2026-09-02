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
  gooseCli;

  String get label => switch (this) {
        AiProvider.anthropic => 'Anthropic (Claude)',
        AiProvider.openAI => 'OpenAI',
        AiProvider.ollama => 'Local LLM (Ollama)',
        AiProvider.gooseCli => 'Goose CLI',
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
