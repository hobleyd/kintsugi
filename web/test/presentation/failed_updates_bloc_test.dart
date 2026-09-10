import 'package:bloc_test/bloc_test.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/patch_failure.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/patch_failure_usecases.dart';
import 'package:kintsugi_web/presentation/applications/failed_updates_bloc.dart';

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
}
