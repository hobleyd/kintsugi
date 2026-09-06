import 'package:equatable/equatable.dart';

/// The Upgrade Scripts screen's whole state. Mirrors `UpgradeScriptsViewDto`.
///
/// The outcome fields are all null on a plain read, and each one is reported as data rather than
/// as a failed request: a GitHub outage must not stop a reviewed script from patching the fleet it
/// was reviewed for, so a failed publish is a note beside a working screen.
class UpgradeScriptsView extends Equatable {
  const UpgradeScriptsView({
    required this.overview,
    this.importResult,
    this.refreshError,
    this.adopted,
    this.adoptError,
    this.tookServerScript,
    this.takeServerScriptError,
  });

  final UpgradeScriptsOverview overview;
  final ImportApprovedScriptsResult? importResult;
  final String? refreshError;
  final AdoptedScript? adopted;
  final String? adoptError;
  final TookServerScript? tookServerScript;
  final String? takeServerScriptError;

  @override
  List<Object?> get props => [
        overview,
        importResult,
        refreshError,
        adopted,
        adoptError,
        tookServerScript,
        takeServerScriptError,
      ];
}

/// Mirrors `UpgradeScriptsOverviewDto`.
class UpgradeScriptsOverview extends Equatable {
  const UpgradeScriptsOverview({
    required this.repository,
    required this.defaultBranch,
    required this.headCommitSha,
    required this.unavailableReason,
    required this.publishingEnabled,
    required this.thisServerFingerprint,
    required this.approved,
    required this.localScripts,
    required this.adoptionCandidates,
  });

  final String repository;

  /// The branch whose protection is the actual trust root for script approval — see the note on
  /// [approved].
  final String? defaultBranch;

  final String? headCommitSha;
  final String? unavailableReason;

  /// False when no script-approval token is configured, in which case signing approves a script on
  /// this server only. Said out loud on screen, because the absence of an audit trail is otherwise
  /// discoverable only by going to look for pull requests that were never opened.
  final bool publishingEnabled;

  /// The fingerprint of this server's own signing key — the only fingerprint whose signatures are
  /// verified against a key this server holds.
  final String thisServerFingerprint;

  /// Approvals imported from the shared repository.
  ///
  /// Every other signer's public key travels in that repository alongside the script it vouches
  /// for, so a signature from one shows who *claims* to have reviewed a script, not that they were
  /// authorized to. Authorization is the repository's branch protection on [defaultBranch] and
  /// nothing else.
  final List<ApprovedScript> approved;

  final List<LocalScript> localScripts;
  final List<AdoptionCandidate> adoptionCandidates;

  /// Scripts no agent will run until a human signs them — the only number here that represents
  /// work outstanding. An application with an unsigned script is not patching at all.
  int get awaitingReview => localScripts.where((s) => !s.signed).length;

  /// Signed rows holding a script this build no longer writes. Deliberately not work outstanding:
  /// they keep running the text that was approved and go on patching.
  int get newerServerScripts =>
      localScripts.where((s) => s.signed && s.newerServerScriptAvailable).length;

  @override
  List<Object?> get props => [
        repository,
        defaultBranch,
        headCommitSha,
        unavailableReason,
        publishingEnabled,
        thisServerFingerprint,
        approved,
        localScripts,
        adoptionCandidates,
      ];
}

/// Mirrors `LocalScriptDto`.
///
/// One entry per *script*, not per upgrade-path row. A package-manager script (Homebrew, App Store,
/// winget, Chocolatey, Flatpak, Snap) is the same bytes for every application the manager handles and one
/// signature covers all of them, so the server lists it once, named for the manager, with
/// [applications] saying how many rows it stands for. An AI-researched script is one application's
/// and appears as itself.
class LocalScript extends Equatable {
  const LocalScript({
    required this.applicationName,
    required this.platform,
    required this.sha256,
    required this.applications,
    required this.signed,
    required this.approvedUpstream,
    required this.newerServerScriptAvailable,
  });

  /// The application, for an AI-researched script; the manager's label ("Homebrew (any managed
  /// application)") for a package-manager one — the same words the imported approvals use.
  final String applicationName;
  final String platform;
  final String sha256;

  /// How many upgrade-path rows hold exactly this script. 1 for an AI-researched one.
  final int applications;
  final bool signed;
  final bool approvedUpstream;

  /// True when this script differs from the one this build would write for its rows. Nothing takes
  /// the newer script by itself — replacing the content of a signed row is replacing what the
  /// fleet's agents execute — so this is what says one exists.
  final bool newerServerScriptAvailable;

  @override
  List<Object?> get props =>
      [applicationName, platform, sha256, applications, signed, approvedUpstream, newerServerScriptAvailable];
}

/// Mirrors `ApprovedScriptDto`.
class ApprovedScript extends Equatable {
  const ApprovedScript({
    required this.sha256,
    required this.platformBucket,
    required this.applicationName,
    required this.signerFingerprint,
    required this.isThisServer,
    required this.signedBy,
    required this.approvedAtUtc,
    required this.sourceCommitSha,
    required this.heldLocally,
  });

  final String sha256;
  final String platformBucket;
  final String applicationName;
  final String signerFingerprint;
  final bool isThisServer;
  final String? signedBy;
  final DateTime approvedAtUtc;
  final String sourceCommitSha;
  final bool heldLocally;

  @override
  List<Object?> get props => [
        sha256,
        platformBucket,
        applicationName,
        signerFingerprint,
        isThisServer,
        signedBy,
        approvedAtUtc,
        sourceCommitSha,
        heldLocally,
      ];
}

/// Mirrors `AdoptionCandidateDto`.
class AdoptionCandidate extends Equatable {
  const AdoptionCandidate({
    required this.applicationName,
    required this.platform,
    required this.sha256,
    required this.signerFingerprint,
    required this.isThisServer,
    required this.signedBy,
    required this.approvedAtUtc,
    required this.replacesExistingScript,
  });

  final String applicationName;
  final String platform;
  final String sha256;
  final String signerFingerprint;
  final bool isThisServer;
  final String? signedBy;
  final DateTime approvedAtUtc;

  /// True when this row already holds unsigned script content that adopting would discard. No
  /// agent is running it — the row is unsigned either way — but someone who hand-wrote a script
  /// here should not discover that from the result, so the button says so.
  final bool replacesExistingScript;

  @override
  List<Object?> get props => [
        applicationName,
        platform,
        sha256,
        signerFingerprint,
        isThisServer,
        signedBy,
        approvedAtUtc,
        replacesExistingScript,
      ];
}

/// Mirrors `ImportApprovedScriptsResultDto`.
class ImportApprovedScriptsResult extends Equatable {
  const ImportApprovedScriptsResult({
    required this.repository,
    required this.commitSha,
    required this.imported,
    required this.alreadyKnown,
    required this.blessed,
    required this.rejected,
  });

  final String repository;
  final String? commitSha;
  final int imported;
  final int alreadyKnown;

  /// Local upgrade paths that gained a signature because their existing script turned out to be
  /// approved content. No script text changed for any of these — the bytes were already here —
  /// which is why this half needs no human decision, and why it is worded differently from
  /// adoption.
  final List<BlessedUpgradePath> blessed;

  /// Every entry passed over, with the reason. Shown rather than counted: a signature that does
  /// not verify and a corpus that is simply small look identical otherwise.
  final List<String> rejected;

  @override
  List<Object?> get props => [repository, commitSha, imported, alreadyKnown, blessed, rejected];
}

class BlessedUpgradePath extends Equatable {
  const BlessedUpgradePath({
    required this.applicationName,
    required this.platform,
    required this.signerFingerprint,
  });

  final String applicationName;
  final String platform;
  final String signerFingerprint;

  @override
  List<Object?> get props => [applicationName, platform, signerFingerprint];
}

/// Mirrors `AdoptApprovedScriptResultDto`.
class AdoptedScript extends Equatable {
  const AdoptedScript({
    required this.applicationName,
    required this.platform,
    required this.sha256,
    required this.signerFingerprint,
  });

  final String applicationName;
  final String platform;
  final String sha256;
  final String signerFingerprint;

  @override
  List<Object?> get props => [applicationName, platform, sha256, signerFingerprint];
}

/// Mirrors `TakeServerWrittenScriptResultDto`.
class TookServerScript extends Equatable {
  const TookServerScript({
    required this.platform,
    required this.changed,
  });

  final String platform;

  /// How many rows took the new text. Zero when every row already held exactly what this build
  /// writes.
  final int changed;

  @override
  List<Object?> get props => [platform, changed];
}
