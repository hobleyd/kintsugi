import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/data/models/agent_package_mapper.dart';
import 'package:kintsugi_web/data/models/host_mapper.dart';
import 'package:kintsugi_web/data/models/patch_failure_mapper.dart';
import 'package:kintsugi_web/data/models/settings_mapper.dart';
import 'package:kintsugi_web/data/models/upgrade_path_mapper.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';

void main() {
  group('hostFromJson', () {
    test('reads HostStatus from the ordinal the server sends', () {
      // HostStatus carries no JSON converter, so System.Text.Json writes its ordinal.
      final host = hostFromJson({'id': 'a', 'hostname': 'alpha', 'status': 2});
      expect(host.status, HostStatus.offline);
    });

    test('keeps a null OS update check distinct from "up to date"', () {
      final unchecked = hostFromJson({'id': 'a', 'operatingSystemUpdateAvailable': null});
      final current = hostFromJson({'id': 'a', 'operatingSystemUpdateAvailable': false});

      // Three states, and the third is not a rounding of the other two: "never checked" has to
      // read differently on screen from "checked, and current".
      expect(unchecked.operatingSystemUpdateAvailable, isNull);
      expect(current.operatingSystemUpdateAvailable, isFalse);
    });

    test('reads agentVersion, and leaves it null when the server omits it', () {
      final reported = hostFromJson({'id': 'a', 'hostname': 'alpha', 'status': 1, 'agentVersion': '0.6.1'});
      final unreported = hostFromJson({'id': 'a', 'hostname': 'alpha', 'status': 1});

      // Null is "this agent has never reported a version" — a host whose agent predates the
      // field — and the screen shows nothing under the status chip rather than an empty bracket.
      expect(reported.agentVersion, '0.6.1');
      expect(unreported.agentVersion, isNull);
    });
  });

  group('upgradePathSummaryFromJson', () {
    test('takes statusKey from the response rather than deriving it', () {
      // The precedence is not obvious — an unsigned script outranks "update available", because an
      // unsigned script is one no agent will run at all — and the same value drives the status
      // filter. Deriving it here would be a second copy free to disagree with the server's.
      final path = upgradePathSummaryFromJson({
        'applicationName': 'Firefox',
        'platform': 'macOS',
        'status': 'Found',
        'statusKey': 'review-sign',
        'method': 'Script',
        'script': '#!/bin/bash',
        'updateAvailableHostCount': 3,
        'checkedUtc': '2026-09-01T00:00:00+00:00',
      });

      expect(path.statusKey, 'review-sign');
      expect(path.isSigned, isFalse);
    });

    test('reads UpgradePathStatus and UpgradeMethod from their names', () {
      final path = upgradePathSummaryFromJson({
        'status': 'NotFound',
        'method': 'PackageManagerCommand',
        'checkedUtc': '2026-09-01T00:00:00+00:00',
      });

      expect(path.status, UpgradePathStatus.notFound);
      expect(path.method, UpgradeMethod.packageManagerCommand);
    });
  });

  group('upgradePathResultFromJson', () {
    test('defaults scriptSigned to false for a freshly researched result', () {
      // RefreshedUpgradePathDto carries no signing or approval fields at all, which is exactly why
      // a fresh result is signable: nothing has reviewed it yet.
      final result = upgradePathResultFromJson({
        'applicationName': 'Firefox',
        'platform': 'macOS',
        'status': 'Found',
        'method': 'Script',
        'script': '#!/bin/bash',
        'checkedUtc': '2026-09-01T00:00:00+00:00',
      });

      expect(result.scriptSigned, isFalse);
      expect(result.isSignable, isTrue);
      expect(result.approvalOutcome, isNull);
    });

    test('is not signable when there is no script to sign', () {
      final result = upgradePathResultFromJson({
        'status': 'Found',
        'method': 'PackageManagerCommand',
        'command': 'brew upgrade firefox',
        'checkedUtc': '2026-09-01T00:00:00+00:00',
      });

      expect(result.isSignable, isFalse);
    });

    test('keeps the raw JSON so an edited result can be saved without losing fields', () {
      final json = {
        'applicationName': 'Firefox',
        'platform': 'macOS',
        'status': 'Found',
        'method': 'Script',
        'script': '#!/bin/bash',
        'checkedUtc': '2026-09-01T00:00:00+00:00',
        'applicationIdentifier': 'org.mozilla.firefox',
      };

      // applicationIdentifier is not modelled by the entity, and the save route accepts it — so it
      // has to survive a round trip through the editor rather than being dropped.
      expect(upgradePathResultFromJson(json).raw['applicationIdentifier'], 'org.mozilla.firefox');
    });
  });

  test('upgradeMethodToJson writes the name, never the ordinal', () {
    expect(upgradeMethodToJson(UpgradeMethod.script), 'Script');
    expect(upgradeMethodToJson(UpgradeMethod.directDownload), 'DirectDownload');
  });

  group('patchingPolicyFromJson', () {
    test('reads the time units from their ordinals', () {
      final policy = patchingPolicyFromJson({
        'intervalValue': 12,
        'intervalUnit': 0,
        'delayValue': 2,
        'delayUnit': 1,
        'maxDelayCount': 0,
      });

      // These ordinals are read by all three Rust agents (policy.rs parses interval_unit as a u8),
      // which is why the wire format here is not ours to change.
      expect(policy.intervalUnit, PatchingTimeUnit.hours);
      expect(policy.delayUnit, PatchingTimeUnit.days);
      expect(policy.maxDelayCount, 0);
    });

    test('falls back to the same defaults the server would use for a missing field', () {
      final policy = patchingPolicyFromJson(const {});
      expect(policy.intervalValue, 7);
      expect(policy.intervalUnit, PatchingTimeUnit.days);
      expect(policy.delayValue, 1);
      expect(policy.maxDelayCount, 3);
    });
  });

  test('aiAgentSettingsFromJson reads the provider ordinal', () {
    expect(aiAgentSettingsFromJson({'provider': 3}).provider, AiProvider.gooseCli);
    // Pinned so that a member inserted rather than appended in either `AiProvider` declaration
    // fails here instead of silently re-mapping every operator's saved provider on screen.
    expect(aiAgentSettingsFromJson({'provider': 4}).provider, AiProvider.claudeAgentSdk);
  });

  test('claudeAgentSdkStatusFromJson reads the probe result', () {
    final status = claudeAgentSdkStatusFromJson({
      'isAvailable': true,
      'version': '2.1.211 (Claude Code)',
      'error': null,
    });

    expect(status.isAvailable, isTrue);
    expect(status.version, '2.1.211 (Claude Code)');
    expect(status.error, isNull);
  });

  test('authenticationSettingsFromJson reads the provider ordinal', () {
    expect(
      authenticationSettingsFromJson({'provider': 1}).provider,
      AuthProvider.microsoftEntra,
    );
  });

  group('auditSettingsFromJson', () {
    test('reads the provider as an ordinal or a name', () {
      // AuditProvider carries no converter, like AuthProvider, so the server writes the ordinal;
      // declaration order in enums.dart is what makes 4 mean Azure Monitor on both ends.
      expect(auditSettingsFromJson({'provider': 4}).provider, AuditProvider.azureMonitor);
      expect(auditSettingsFromJson({'provider': 'SplunkHec'}).provider, AuditProvider.splunkHec);
    });

    test('never carries the secret, only whether one is stored', () {
      final settings = auditSettingsFromJson({'provider': 0, 'hasSecret': true, 'region': 'datadoghq.eu'});

      expect(settings.hasSecret, isTrue);
      expect(settings.region, 'datadoghq.eu');
      expect(settings.endpoint, isNull);
    });

    test('an unconfigured server reads as Datadog, disabled', () {
      final settings = auditSettingsFromJson(const {});

      // Matches AuditSettingsDto.NotConfigured(), so the form opens on the same provider whether
      // the server answered explicitly or the mapper filled the gap.
      expect(settings.provider, AuditProvider.datadog);
      expect(settings.isEnabled, isFalse);
      expect(settings.hasSecret, isFalse);
    });
  });

  group('vantaSettingsFromJson', () {
    test('never carries the client secret, only whether one is stored', () {
      final settings = vantaSettingsFromJson({'hasClientSecret': true, 'clientId': 'abc'});

      // The DTO has no secret field at all, which is what lets the form honestly offer "leave
      // blank to keep the existing one".
      expect(settings.hasClientSecret, isTrue);
      expect(settings.clientId, 'abc');
    });

    test('falls back to the same defaults the server resolves', () {
      final settings = vantaSettingsFromJson(const {});

      expect(settings.severity, 5.0);
      expect(settings.syncIntervalHours, 24);
      expect(settings.enabled, isFalse);
      expect(settings.isConfigured, isFalse);
    });
  });

  group('vantaSyncStatusFromJson', () {
    test('keeps "never run" distinct from "the last run failed"', () {
      final neverRun = vantaSyncStatusFromJson(const {'running': false});
      final failed = vantaSyncStatusFromJson(const {'running': false, 'lastRunSucceeded': false});

      // The status is held in memory on the server, so a restart resets it to null. That is a very
      // different thing to be looking at from a failure, and the screen says so.
      expect(neverRun.lastRunSucceeded, isNull);
      expect(failed.lastRunSucceeded, isFalse);
    });

    test('parses the timestamps the server sends as UTC', () {
      final status = vantaSyncStatusFromJson(const {
        'running': false,
        'completedUtc': '2026-09-03T04:05:06+00:00',
        'componentCount': 3,
        'packageCount': 7,
      });

      expect(status.completedUtc, isNotNull);
      expect(status.componentCount, 3);
      expect(status.packageCount, 7);
    });
  });

  group('sourceRowFromJson', () {
    test('reads every newer release with its notes, in the order the server ranked them', () {
      final row = sourceRowFromJson(const {
        'platform': 'macos',
        'availableVersion': '0.7.0',
        'publishedVersion': '0.5.0',
        'isNewer': true,
        'newerReleases': [
          {'version': '0.7.0', 'releaseNotes': 'Seventh.'},
          {'version': '0.6.0', 'releaseNotes': null},
        ],
      });

      // The server sorts highest first; the client keeps that order rather than re-deriving it,
      // which would be a second version comparison free to disagree with AgentPackageReleases.
      expect(row.newerReleases.map((r) => r.version), ['0.7.0', '0.6.0']);
      expect(row.newerReleases.first.releaseNotes, 'Seventh.');
      expect(row.newerReleases.last.releaseNotes, isNull);
    });

    test('an up-to-date platform has an empty list rather than a missing one', () {
      final row = sourceRowFromJson(const {
        'platform': 'linux',
        'availableVersion': '0.5.0',
        'publishedVersion': '0.5.0',
        'isNewer': false,
        'newerReleases': <Object>[],
      });

      expect(row.newerReleases, isEmpty);
    });
  });

  group('upgradePathResultFromJson', () {
    /// Hand-mirrored from `UpgradePathResultDto` with nothing in CI cross-checking the two, and a
    /// misspelling here reads as 0 rather than failing — which would silently make a repair look
    /// like it cleared nothing. See `.claude/rules/hand-mirrored-dtos.md`.
    test('reads how many patch failures a repair signature cleared', () {
      final result = upgradePathResultFromJson(const {
        'applicationName': 'Ollama',
        'platform': 'macOS',
        'status': 'Found',
        'method': 'Script',
        'script': '#!/bin/bash',
        'scriptSigned': true,
        'clearedPatchFailures': 4,
      });

      expect(result.clearedPatchFailures, 4);
    });

    test('reads an ordinary signature, which clears nothing, as zero', () {
      final result = upgradePathResultFromJson(const {
        'applicationName': 'Ollama',
        'platform': 'macOS',
        'status': 'Found',
        'method': 'Script',
        'script': '#!/bin/bash',
        'scriptSigned': true,
      });

      expect(result.clearedPatchFailures, 0);
    });
  });

  group('patchFailureFromJson', () {
    Map<String, dynamic> body([Map<String, dynamic> overrides = const {}]) => {
          'id': 'f1',
          'hostId': 'h1',
          'hostname': 'mac-1',
          'serialNumber': 'C02',
          'applicationName': 'Ollama',
          'platform': 'macOS',
          'installedVersion': '0.32.14',
          'attemptedVersion': '0.33.3',
          'details': 'exited with 1: Permission denied',
          'firstFailedUtc': '2026-09-07T02:00:00+00:00',
          'lastFailedUtc': '2026-09-09T02:00:00+00:00',
          'failureCount': 4,
          'resolution': 0,
          'resolvedUtc': null,
          'hasScript': true,
          'scriptSigned': true,
          'method': 'Script',
          'canFix': true,
          ...overrides,
        };

    test('reads PatchFailureResolution from the ordinal the server sends', () {
      // PatchFailureResolution carries no JSON converter, so System.Text.Json writes its ordinal —
      // which is what makes declaration order in enums.dart load-bearing.
      expect(patchFailureFromJson(body()).resolution, PatchFailureResolution.outstanding);
      expect(
        patchFailureFromJson(body({'resolution': 1})).resolution,
        PatchFailureResolution.patchSucceeded,
      );
      expect(
        patchFailureFromJson(body({'resolution': 2})).resolution,
        PatchFailureResolution.dismissed,
      );
      // Appended, never inserted — an ordinal indexes into declaration order, so a member added
      // anywhere but the end silently re-maps every value already stored.
      expect(
        patchFailureFromJson(body({'resolution': 3})).resolution,
        PatchFailureResolution.scriptRepaired,
      );
    });

    /// A resolution this client is too old to know about must degrade to something harmless rather
    /// than blanking the screen — enumFromJson's fallback, pinned here because the fallback for
    /// this enum is `outstanding`, which is the safe way to be wrong: a row shown that should have
    /// been hidden, rather than a fix silently dropped from the queue.
    test('falls back to outstanding for a resolution it does not know', () {
      expect(
        patchFailureFromJson(body({'resolution': 99})).resolution,
        PatchFailureResolution.outstanding,
      );
    });

    test('keeps the counts and both dates, which are what tell a blip from a broken script', () {
      final failure = patchFailureFromJson(body());

      expect(failure.failureCount, 4);
      expect(failure.isRepeating, isTrue);
      expect(failure.firstFailedUtc.toUtc(), DateTime.utc(2026, 9, 7, 2));
      expect(failure.lastFailedUtc.toUtc(), DateTime.utc(2026, 9, 9, 2));
    });

    /// A failure whose upgrade path has since been deleted still has to arrive — losing it would
    /// hide a real failure. The screen shows it as one it cannot offer a fix for.
    test('reads a failure with no resolved platform', () {
      final failure = patchFailureFromJson(body({'platform': null, 'canFix': false}));

      expect(failure.platform, isNull);
      expect(failure.canFix, isFalse);
    });

    /// canFix is computed server-side from a join the client cannot see. The fallback exists only
    /// for a response predating the field, and must agree with the server's own rule.
    test('falls back to platform-and-script when the server sent no canFix', () {
      final withScript = body()..remove('canFix');
      expect(patchFailureFromJson(withScript).canFix, isTrue);

      final withoutScript = body({'hasScript': false})..remove('canFix');
      expect(patchFailureFromJson(withoutScript).canFix, isFalse);
    });
  });

}
