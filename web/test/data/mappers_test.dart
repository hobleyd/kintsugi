import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/data/models/host_mapper.dart';
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
}
