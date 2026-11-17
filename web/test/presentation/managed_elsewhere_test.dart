import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/data/models/managed_application_mapper.dart';
import 'package:kintsugi_web/domain/entities/application.dart';
import 'package:kintsugi_web/domain/entities/managed_application.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/managed_application_usecases.dart';
import 'package:kintsugi_web/presentation/applications/applications_bloc.dart';
import 'package:kintsugi_web/presentation/applications/managed_elsewhere_screen.dart';

class _FakeRepository implements ManagedApplicationRepository {
  _FakeRepository(this.state);

  ManagedApplications state;
  final calls = <String>[];

  @override
  Future<ManagedApplications> read() async => state;

  @override
  Future<ManagedApplications> mark({required List<String> names, required String owner}) async {
    calls.add('mark ${names.join(',')} $owner');
    return state;
  }

  @override
  Future<ManagedApplications> unmark(String applicationName) async {
    calls.add('unmark $applicationName');
    state = ManagedApplications(
      applications: [for (final a in state.applications) if (a.applicationName != applicationName) a],
      owners: state.owners,
    );
    return state;
  }

  @override
  Future<ManagedApplications> addOwner(String name) async {
    calls.add('addOwner $name');
    state = ManagedApplications(applications: state.applications, owners: [...state.owners, name]);
    return state;
  }

  @override
  Future<ManagedApplications> removeOwner(String name) async {
    calls.add('removeOwner $name');
    throw const ApiException('Still in use.');
  }
}

const _initial = ManagedApplications(
  applications: [
    ManagedApplication(applicationName: 'Slack', owner: 'Jamf Pro', hostCount: 3),
    ManagedApplication(applicationName: 'Zoom', owner: 'Jamf Pro', hostCount: 2),
  ],
  owners: ['CrowdStrike', 'Jamf Pro'],
);

Future<_FakeRepository> _pump(WidgetTester tester) async {
  final repository = _FakeRepository(_initial);
  locator.registerSingleton(GetManagedApplications(repository));
  locator.registerSingleton(MarkApplicationsManaged(repository));
  locator.registerSingleton(UnmarkApplicationManaged(repository));
  locator.registerSingleton(AddManagedOwner(repository));
  locator.registerSingleton(RemoveManagedOwner(repository));

  tester.view.physicalSize = const Size(1400, 1600);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);

  await tester.pumpWidget(const MaterialApp(home: Scaffold(body: ManagedElsewhereScreen())));
  await tester.pumpAndSettle();
  return repository;
}

void main() {
  tearDown(() => locator.reset());

  test('the mapper reads applications and owners', () {
    final data = managedApplicationsFromJson({
      'applications': [
        {'applicationName': 'Slack', 'owner': 'Jamf Pro', 'hostCount': 3},
      ],
      'owners': ['Jamf Pro'],
    });

    expect(data.applications.single.applicationName, 'Slack');
    expect(data.applications.single.hostCount, 3);
    expect(data.owners, ['Jamf Pro']);
  });

  test('an owner is in use whatever its case', () {
    expect(_initial.ownerIsInUse('jamf pro'), isTrue);
    expect(_initial.ownerIsInUse('CrowdStrike'), isFalse);
  });

  test('a managed application reads as managed-elsewhere, whatever was researched for it', () {
    const application = ApplicationRow(
      name: 'Slack',
      hostCount: 1,
      hostNames: ['a'],
      upgradePaths: [],
      children: [],
      managedBy: 'Jamf Pro',
    );

    expect(
      const ApplicationTableRow(application: application, upgradePath: null).statusKey,
      'managed-elsewhere',
    );
  });

  testWidgets('the page groups applications under their owner', (tester) async {
    await _pump(tester);

    expect(find.text('Jamf Pro · 2'), findsOneWidget);
    expect(find.text('Slack'), findsOneWidget);
    expect(find.text('Zoom'), findsOneWidget);
  });

  testWidgets('releasing an application asks the server and removes it', (tester) async {
    final repository = await _pump(tester);

    await tester.tap(find.text('STOP TREATING AS MANAGED').first);
    await tester.pumpAndSettle();

    expect(repository.calls, ['unmark Slack']);
    expect(find.text('Slack'), findsNothing);
  });

  testWidgets('a tool that owns an application cannot be removed from the list', (tester) async {
    final repository = await _pump(tester);

    final chips = tester.widgetList<InputChip>(find.byType(InputChip)).toList();
    final jamf = chips.firstWhere((c) => (c.label as Text).data == 'Jamf Pro');
    final crowdstrike = chips.firstWhere((c) => (c.label as Text).data == 'CrowdStrike');

    expect(jamf.onDeleted, isNull);
    expect(crowdstrike.onDeleted, isNotNull);
    expect(repository.calls, isEmpty);
  });

  testWidgets('adding a tool puts it on the list', (tester) async {
    final repository = await _pump(tester);

    await tester.enterText(find.byType(TextField), 'Intune');
    await tester.tap(find.text('ADD'));
    await tester.pumpAndSettle();

    expect(repository.calls, ['addOwner Intune']);
    expect(find.text('Intune'), findsOneWidget);
  });
}
