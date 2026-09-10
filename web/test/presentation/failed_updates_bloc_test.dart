import 'package:bloc_test/bloc_test.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/patch_failure.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/patch_failure_usecases.dart';
import 'package:kintsugi_web/presentation/applications/failed_updates_bloc.dart';
import 'package:kintsugi_web/presentation/applications/instructions_panel_bloc.dart';

class FakePatchFailureRepository implements PatchFailureRepository {
  FakePatchFailureRepository(this.failures);

  List<PatchFailure> failures;
  final dismissed = <String>[];

  @override
  Future<List<PatchFailure>> list() async => failures;

  @override
  Future<void> dismiss(String id) async {
    dismissed.add(id);
    failures = [
      for (final failure in failures)
        if (failure.id == id) _copyDismissed(failure) else failure,
    ];
  }

  static PatchFailure _copyDismissed(PatchFailure failure) => failure_(
        id: failure.id,
        applicationName: failure.applicationName,
        hostname: failure.hostname,
        resolution: PatchFailureResolution.dismissed,
      );
}

PatchFailure failure_({
  required String id,
  String applicationName = 'Ollama',
  String hostname = 'mac-1',
  String? platform = 'macOS',
  String details = 'exited with 1: Permission denied',
  int failureCount = 1,
  PatchFailureResolution resolution = PatchFailureResolution.outstanding,
  bool hasScript = true,
}) =>
    PatchFailure(
      id: id,
      hostId: 'host-$id',
      hostname: hostname,
      serialNumber: 'SERIAL-$id',
      applicationName: applicationName,
      platform: platform,
      installedVersion: '0.32.14',
      attemptedVersion: '0.33.3',
      details: details,
      firstFailedUtc: DateTime.utc(2026, 9, 7),
      lastFailedUtc: DateTime.utc(2026, 9, 9),
      failureCount: failureCount,
      resolution: resolution,
      resolvedUtc: resolution == PatchFailureResolution.outstanding ? null : DateTime.utc(2026, 9, 9),
      hasScript: hasScript,
      scriptSigned: true,
      method: UpgradeMethod.script,
      canFix: platform != null && hasScript,
    );

/// What the fix panel hands back after a signature. `approvalDescription` is the sentence the panel
/// would have shown about the pull request, which the screen re-states because signing closes that
/// panel in the same frame.
SignedScriptOutcome signed_({
  int clearedPatchFailures = 1,
  String approvalDescription = 'Opened a pull request for review.',
  String? approvalPullRequestUrl = 'https://example.invalid/pull/1',
}) =>
    SignedScriptOutcome(
      clearedPatchFailures: clearedPatchFailures,
      approvalDescription: approvalDescription,
      approvalPullRequestUrl: approvalPullRequestUrl,
    );

FailedUpdatesBloc blocFor(FakePatchFailureRepository repository) => FailedUpdatesBloc(
      getFailures: GetPatchFailures(repository),
      dismissFailure: DismissPatchFailure(repository),
    );

void main() {
  group('the default view', () {
    /// Outstanding by default: the settled rows are history rather than work, and a queue that
    /// opened showing everything ever reported would stop being a queue on the first fixed script.
    blocTest<FailedUpdatesBloc, FailedUpdatesState>(
      'shows outstanding failures and hides settled ones',
      build: () => blocFor(FakePatchFailureRepository([
        failure_(id: '1'),
        failure_(id: '2', resolution: PatchFailureResolution.patchSucceeded),
      ])),
      act: (bloc) => bloc.add(const FailedUpdatesRequested()),
      wait: const Duration(milliseconds: 10),
      verify: (bloc) {
        expect(bloc.state.visibleRows.map((f) => f.id), ['1']);
        expect(bloc.state.outstandingCount, 1);
      },
    );

    blocTest<FailedUpdatesBloc, FailedUpdatesState>(
      'shows the settled ones when asked for them',
      build: () => blocFor(FakePatchFailureRepository([
        failure_(id: '1'),
        failure_(id: '2', resolution: PatchFailureResolution.patchSucceeded),
      ])),
      act: (bloc) => bloc
        ..add(const FailedUpdatesRequested())
        ..add(const FailedUpdatesFiltersChanged(FailedUpdateFilters(resolutionKey: 'settled'))),
      wait: const Duration(milliseconds: 10),
      verify: (bloc) => expect(bloc.state.visibleRows.map((f) => f.id), ['2']),
    );
  });

  group('search', () {
    /// The output is searchable, not just the names: "Permission denied" across four hosts is one
    /// problem, and finding it that way is most of what this screen is for.
    blocTest<FailedUpdatesBloc, FailedUpdatesState>(
      'matches on the failure output as well as the application and the host',
      build: () => blocFor(FakePatchFailureRepository([
        failure_(id: '1', details: 'exited with 1: Permission denied'),
        failure_(id: '2', applicationName: 'Firefox', details: 'exited with 2: no such file'),
      ])),
      act: (bloc) => bloc
        ..add(const FailedUpdatesRequested())
        ..add(const FailedUpdatesFiltersChanged(FailedUpdateFilters(search: 'permission'))),
      wait: const Duration(milliseconds: 10),
      verify: (bloc) => expect(bloc.state.visibleRows.map((f) => f.id), ['1']),
    );
  });

  group('the fix panel', () {
    blocTest<FailedUpdatesBloc, FailedUpdatesState>(
      'opens one row at a time',
      build: () => blocFor(FakePatchFailureRepository([failure_(id: '1'), failure_(id: '2')])),
      act: (bloc) => bloc
        ..add(const FailedUpdatesRequested())
        ..add(const FailedUpdateRowExpansionToggled('1'))
        ..add(const FailedUpdateRowExpansionToggled('2')),
      wait: const Duration(milliseconds: 10),
      verify: (bloc) => expect(bloc.state.expandedId, '2'),
    );

    blocTest<FailedUpdatesBloc, FailedUpdatesState>(
      'closes again when the same row is pressed twice',
      build: () => blocFor(FakePatchFailureRepository([failure_(id: '1')])),
      act: (bloc) => bloc
        ..add(const FailedUpdatesRequested())
        ..add(const FailedUpdateRowExpansionToggled('1'))
        ..add(const FailedUpdateRowExpansionToggled('1')),
      wait: const Duration(milliseconds: 10),
      verify: (bloc) => expect(bloc.state.expandedId, isNull),
    );

    /// A row with no stored script has nothing the panel can act on — the screen says so instead of
    /// offering an editor over nothing.
    test('is not offered for a failure whose upgrade path has gone', () {
      expect(failure_(id: '1', platform: null).canFix, isFalse);
      expect(failure_(id: '1', hasScript: false).canFix, isFalse);
      expect(failure_(id: '1').canFix, isTrue);
    });

    /// A package manager's script is one fixed text shared by every row of its bucket, so the panel
    /// offers hand-editing but not AI repair — and the screen says which, rather than leaving a
    /// missing button to be noticed. Read off the bucket's own `pm:` prefix, the way the server
    /// writes it (`PlatformBucket.ForPackageManager`).
    test('knows a package-manager row from an AI-researched one', () {
      expect(failure_(id: '1', platform: 'pm:Homebrew').isPackageManagerManaged, isTrue);
      expect(failure_(id: '1', platform: 'pm:winget').isPackageManagerManaged, isTrue);
      expect(failure_(id: '1', platform: 'macOS').isPackageManagerManaged, isFalse);
      expect(failure_(id: '1', platform: null).isPackageManagerManaged, isFalse);
    });
  });

  group('dismissing', () {
    blocTest<FailedUpdatesBloc, FailedUpdatesState>(
      're-reads the list so the row leaves the default view',
      build: () => blocFor(FakePatchFailureRepository([failure_(id: '1'), failure_(id: '2')])),
      act: (bloc) => bloc
        ..add(const FailedUpdatesRequested())
        ..add(const FailedUpdateDismissed('1')),
      wait: const Duration(milliseconds: 20),
      verify: (bloc) {
        expect(bloc.state.visibleRows.map((f) => f.id), ['2']);
        expect(bloc.state.dismissingIds, isEmpty);
      },
    );

    /// A panel left expanded against a row that has just left the view is a background refresh
    /// polling for something nobody can see.
    blocTest<FailedUpdatesBloc, FailedUpdatesState>(
      'closes the fix panel it was dismissing from',
      build: () => blocFor(FakePatchFailureRepository([failure_(id: '1')])),
      act: (bloc) => bloc
        ..add(const FailedUpdatesRequested())
        ..add(const FailedUpdateRowExpansionToggled('1'))
        ..add(const FailedUpdateDismissed('1')),
      wait: const Duration(milliseconds: 20),
      verify: (bloc) => expect(bloc.state.expandedId, isNull),
    );
  });

  group('signing a repair from a row', () {
    /// The whole point of the signal: the script that failed no longer exists, so the row is stale
    /// from that moment. It is not a claim the fix *worked* — the next patch cycle decides that,
    /// and the row comes back if it did not.
    blocTest<FailedUpdatesBloc, FailedUpdatesState>(
      'takes the row off the queue and closes its panel',
      build: () => blocFor(FakePatchFailureRepository([
        failure_(id: '1', resolution: PatchFailureResolution.scriptRepaired),
        failure_(id: '2'),
      ])),
      act: (bloc) => bloc
        ..add(const FailedUpdatesRequested())
        ..add(const FailedUpdateRowExpansionToggled('1'))
        ..add(FailedUpdateScriptSigned('1', signed_())),
      wait: const Duration(milliseconds: 20),
      verify: (bloc) {
        expect(bloc.state.visibleRows.map((f) => f.id), ['2']);
        expect(bloc.state.expandedId, isNull);
      },
    );

    /// Signing clears every host failing on that same script, not just the row that was open —
    /// a wider action than the operator named, so the screen says the number rather than doing it
    /// silently.
    blocTest<FailedUpdatesBloc, FailedUpdatesState>(
      'says how many hosts it cleared when it cleared more than the open row',
      build: () => blocFor(FakePatchFailureRepository([failure_(id: '1')])),
      act: (bloc) => bloc
        ..add(const FailedUpdatesRequested())
        ..add(FailedUpdateScriptSigned('1', signed_(clearedPatchFailures: 4))),
      wait: const Duration(milliseconds: 20),
      verify: (bloc) {
        expect(bloc.state.notice, contains('4 failures'));
        expect(bloc.state.notice, contains('Ollama'));
        // And says the fix is unproven, which is the whole arrangement.
        expect(bloc.state.notice, contains('comes back'));
      },
    );

    blocTest<FailedUpdatesBloc, FailedUpdatesState>(
      'does not pluralise a single cleared failure into a fleet',
      build: () => blocFor(FakePatchFailureRepository([failure_(id: '1')])),
      act: (bloc) => bloc
        ..add(const FailedUpdatesRequested())
        ..add(FailedUpdateScriptSigned('1', signed_())),
      wait: const Duration(milliseconds: 20),
      verify: (bloc) {
        expect(bloc.state.notice, contains('Cleared its failure'));
        expect(bloc.state.notice, isNot(contains('across the hosts')));
      },
    );

    /// Signing closes the panel that was showing "Signed. Opened a pull request" and its link, in
    /// the same frame they appeared. The pull request is the durable record of the review, so the
    /// screen re-states it at page level — otherwise the one artifact of the approval is destroyed
    /// by the row leaving the table.
    blocTest<FailedUpdatesBloc, FailedUpdatesState>(
      'keeps the approval pull request reachable after the panel closes',
      build: () => blocFor(FakePatchFailureRepository([failure_(id: '1')])),
      act: (bloc) => bloc
        ..add(const FailedUpdatesRequested())
        ..add(FailedUpdateScriptSigned('1', signed_())),
      wait: const Duration(milliseconds: 20),
      verify: (bloc) {
        expect(bloc.state.notice, contains('Opened a pull request'));
        expect(bloc.state.noticeLinkUrl, 'https://example.invalid/pull/1');
      },
    );

    /// A banner describes one action. Left up, "Signed the repaired script for Ollama" stayed on
    /// screen through every later filter change and background reload, announcing something several
    /// interactions old as though it had just happened.
    /// Awaited between events on purpose. Bloc runs handlers for *different* event types
    /// concurrently, so firing these back to back lets the synchronous filter change finish before
    /// the asynchronous sign handler has emitted anything — which passes for the wrong reason and
    /// tests nothing. What is being pinned is the ordinary sequence: banner appears, then the
    /// operator does something else.
    blocTest<FailedUpdatesBloc, FailedUpdatesState>(
      'clears the banner, and its link, when the table is narrowed afterwards',
      build: () => blocFor(FakePatchFailureRepository([failure_(id: '1')])),
      act: (bloc) async {
        bloc.add(const FailedUpdatesRequested());
        await Future<void>.delayed(const Duration(milliseconds: 10));
        bloc.add(FailedUpdateScriptSigned('1', signed_()));
        await Future<void>.delayed(const Duration(milliseconds: 10));
        expect(bloc.state.notice, isNotNull, reason: 'the banner has to exist before it can clear');
        bloc.add(const FailedUpdatesFiltersChanged(FailedUpdateFilters(search: 'ollama')));
      },
      wait: const Duration(milliseconds: 20),
      verify: (bloc) {
        expect(bloc.state.notice, isNull);
        expect(bloc.state.noticeLinkUrl, isNull);
      },
    );

    blocTest<FailedUpdatesBloc, FailedUpdatesState>(
      'clears the banner on the next reload rather than carrying it forward',
      build: () => blocFor(FakePatchFailureRepository([failure_(id: '1')])),
      act: (bloc) async {
        bloc.add(const FailedUpdatesRequested());
        await Future<void>.delayed(const Duration(milliseconds: 10));
        bloc.add(FailedUpdateScriptSigned('1', signed_()));
        await Future<void>.delayed(const Duration(milliseconds: 10));
        bloc.add(const FailedUpdatesRequested(showSpinner: false));
      },
      wait: const Duration(milliseconds: 20),
      verify: (bloc) => expect(bloc.state.notice, isNull),
    );
  });

}
