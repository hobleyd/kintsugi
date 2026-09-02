import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/domain/entities/application.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/upgrade_path.dart';
import 'package:kintsugi_web/presentation/applications/applications_bloc.dart';

UpgradePathSummary path({
  required String statusKey,
  List<String> hostNamesNeedingUpdate = const [],
  String platform = 'macOS',
}) =>
    UpgradePathSummary(
      applicationName: 'Firefox',
      platform: platform,
      status: UpgradePathStatus.found,
      statusKey: statusKey,
      latestVersion: '142.0',
      method: UpgradeMethod.script,
      downloadUrl: null,
      command: null,
      instructions: null,
      sourceUrl: null,
      notes: null,
      checkedUtc: DateTime.utc(2026, 9, 1),
      hostCount: 2,
      upToDateHostCount: 1,
      updateAvailableHostCount: hostNamesNeedingUpdate.length,
      hostNamesNeedingUpdate: hostNamesNeedingUpdate,
      script: '#!/bin/bash',
      scriptSignature: 'signed',
    );

ApplicationTableRow row({
  required String statusKey,
  List<String> installedOn = const ['alpha', 'beta'],
  List<String> needingUpdate = const [],
}) =>
    ApplicationTableRow(
      application: ApplicationRow(
        name: 'Firefox',
        hostCount: installedOn.length,
        hostNames: installedOn,
        upgradePaths: [path(statusKey: statusKey, hostNamesNeedingUpdate: needingUpdate)],
        children: const [],
      ),
      upgradePath: path(statusKey: statusKey, hostNamesNeedingUpdate: needingUpdate),
      isChild: false,
    );

void main() {
  group('search', () {
    test('matches case-insensitively on part of the name', () {
      const filters = ApplicationFilters(search: 'fire');
      expect(filters.matches(row(statusKey: 'up-to-date')), isTrue);
    });

    test('excludes a row whose name does not contain the term', () {
      const filters = ApplicationFilters(search: 'chrome');
      expect(filters.matches(row(statusKey: 'up-to-date')), isFalse);
    });
  });

  group('status', () {
    test('matches on the key the server computed', () {
      expect(
        const ApplicationFilters(statusKey: 'review-sign').matches(row(statusKey: 'review-sign')),
        isTrue,
      );
      expect(
        const ApplicationFilters(statusKey: 'review-sign').matches(row(statusKey: 'up-to-date')),
        isFalse,
      );
    });
  });

  group('host', () {
    test('matches an installed host, case-insensitively', () {
      expect(
        const ApplicationFilters(hostName: 'ALPHA').matches(row(statusKey: 'up-to-date')),
        isTrue,
      );
    });

    test('excludes a host the application is not installed on', () {
      expect(
        const ApplicationFilters(hostName: 'gamma').matches(row(statusKey: 'up-to-date')),
        isFalse,
      );
    });
  });

  group('host combined with update-available', () {
    // The subtle rule, and the reason the API sends hostNamesNeedingUpdate at all. "Update
    // Available" is fleet-wide — true if ANY host is behind — so testing only "is it installed
    // here" would surface applications the chosen host is already current on, just because some
    // other host is not.
    test('keeps a row only when the chosen host is one of the hosts behind', () {
      final outdatedOnBeta = row(statusKey: 'update-available', needingUpdate: ['beta']);

      expect(
        const ApplicationFilters(statusKey: 'update-available', hostName: 'beta')
            .matches(outdatedOnBeta),
        isTrue,
      );

      // alpha has Firefox installed and is up to date on it. Filtering by alpha and "update
      // available" must not list it.
      expect(
        const ApplicationFilters(statusKey: 'update-available', hostName: 'alpha')
            .matches(outdatedOnBeta),
        isFalse,
      );
    });

    test('is case-insensitive about the outdated host names too', () {
      final outdatedOnBeta = row(statusKey: 'update-available', needingUpdate: ['Beta']);
      expect(
        const ApplicationFilters(statusKey: 'update-available', hostName: 'beta')
            .matches(outdatedOnBeta),
        isTrue,
      );
    });
  });

  group('isActive', () {
    test('is false only when nothing is filtered', () {
      expect(const ApplicationFilters().isActive, isFalse);
      expect(const ApplicationFilters(search: 'x').isActive, isTrue);
      expect(const ApplicationFilters(statusKey: 'up-to-date').isActive, isTrue);
      expect(const ApplicationFilters(hostName: 'alpha').isActive, isTrue);
    });
  });

  group('ApplicationsState.allRows', () {
    test('produces one row per platform, and one for an application with no researched path', () {
      final state = ApplicationsState(
        overview: ApplicationOverview(
          applications: [
            ApplicationRow(
              name: 'Firefox',
              hostCount: 2,
              hostNames: const ['alpha', 'beta'],
              upgradePaths: [
                path(statusKey: 'up-to-date'),
                path(statusKey: 'update-available', platform: 'Windows'),
              ],
              children: const [],
            ),
            const ApplicationRow(
              name: 'Nothing Researched',
              hostCount: 1,
              hostNames: ['alpha'],
              upgradePaths: [],
              children: [],
            ),
          ],
          totalApplicationCount: 2,
          allHostNames: const ['alpha', 'beta'],
        ),
      );

      expect(state.allRows.length, 3);
      expect(state.allRows.map((r) => r.platform), ['macOS', 'Windows', '']);
      expect(state.allRows.last.statusKey, 'not-checked');
    });

    test('flattens a package manager children directly after their parent', () {
      final state = ApplicationsState(
        overview: ApplicationOverview(
          applications: [
            ApplicationRow(
              name: 'Homebrew',
              hostCount: 1,
              hostNames: const ['alpha'],
              upgradePaths: [path(statusKey: 'up-to-date', platform: 'pm:Homebrew')],
              children: const [
                ApplicationRow(
                  name: 'firefox',
                  hostCount: 1,
                  hostNames: ['alpha'],
                  upgradePaths: [],
                  children: [],
                ),
              ],
            ),
          ],
          totalApplicationCount: 2,
          allHostNames: const ['alpha'],
        ),
      );

      expect(state.allRows.map((r) => r.application.name), ['Homebrew', 'firefox']);
      expect(state.allRows.map((r) => r.isChild), [false, true]);
    });
  });
}
