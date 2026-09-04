import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/domain/entities/agent_package.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/client_usecases.dart';
import 'package:kintsugi_web/presentation/clients/clients_screen.dart';

/// The release-notes expander under each Clients row.
///
/// Pumps the real [ClientsScreen] against a fake repository, registered in [locator] the way
/// `injection.dart` registers the real one. What this pins is the surface: that the chevron opens
/// a panel listing every build newer than the row's own, highest first, with each one's notes; that
/// an up-to-date row says so rather than opening onto nothing; and that only one row is open at a
/// time, the way the Applications screen's instructions panel behaves.
void main() {
  late FakeAgentPackageRepository packages;

  setUp(() {
    packages = FakeAgentPackageRepository(clientsView());
    locator
      ..registerSingleton(GetClientsView(packages))
      ..registerSingleton(RefreshClients(packages));
  });

  tearDown(() => locator.reset());

  final open = find.byTooltip('Release notes for newer builds');
  final close = find.byTooltip('Hide release notes');

  Future<void> pumpScreen(WidgetTester tester) async {
    tester.view.physicalSize = const Size(1400, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        home: const Scaffold(body: ClientsScreen()),
      ),
    );
    await tester.pumpAndSettle();
  }

  testWidgets('every row has a collapsed chevron and nothing is expanded until one is pressed', (tester) async {
    await pumpScreen(tester);

    expect(open, findsNWidgets(2));
    expect(close, findsNothing);
    expect(find.text('Seventh.'), findsNothing);
  });

  testWidgets('expanding a row behind upstream lists each newer build, highest first, with its notes',
      (tester) async {
    await pumpScreen(tester);

    // The macOS row is first in the table, as it is in the view.
    await tester.tap(open.first);
    await tester.pumpAndSettle();

    expect(close, findsOneWidget);
    expect(find.text('v0.7.0'), findsOneWidget);
    expect(find.text('v0.6.0'), findsOneWidget);
    expect(find.text('Seventh.'), findsOneWidget);
    expect(find.text('No release notes were written for this build.'), findsOneWidget);
    // The published build's own notes are the Notes column's job, not the panel's.
    expect(find.text('v0.5.0'), findsNothing);

    // Highest first: the newest build is what a refresh publishes, so it is read first.
    expect(tester.getTopLeft(find.text('v0.7.0')).dy, lessThan(tester.getTopLeft(find.text('v0.6.0')).dy));

    await tester.tap(close);
    await tester.pumpAndSettle();
    expect(find.text('Seventh.'), findsNothing);
  });

  testWidgets('an up-to-date row says so rather than opening onto an empty panel', (tester) async {
    await pumpScreen(tester);

    await tester.tap(open.last);
    await tester.pumpAndSettle();

    expect(find.text('v0.5.0 is the newest linux build in hobleyd/kintsugi.'), findsOneWidget);
  });

  testWidgets('opening a second row closes the first', (tester) async {
    await pumpScreen(tester);

    await tester.tap(open.first);
    await tester.pumpAndSettle();
    expect(find.text('Seventh.'), findsOneWidget);

    // The still-collapsed chevron is the other row's.
    await tester.tap(open);
    await tester.pumpAndSettle();

    expect(find.text('Seventh.'), findsNothing);
    expect(find.text('v0.5.0 is the newest linux build in hobleyd/kintsugi.'), findsOneWidget);
    expect(close, findsOneWidget);
  });

  testWidgets('an unreachable upstream is explained in the panel, not shown as no notes', (tester) async {
    packages.current = clientsView(
      sourceStatus: const AgentPackageSourceStatus(
        sourceDescription: 'hobleyd/kintsugi',
        platforms: [],
        unavailableReason: 'rate limited',
      ),
    );
    await pumpScreen(tester);

    await tester.tap(open.first);
    await tester.pumpAndSettle();

    expect(
      find.text('Could not check hobleyd/kintsugi for builds newer than v0.5.0: rate limited'),
      findsOneWidget,
    );
  });
}

ClientsView clientsView({AgentPackageSourceStatus? sourceStatus}) => ClientsView(
      packages: [
        package('macos'),
        package('linux'),
      ],
      sourceStatus: sourceStatus ??
          const AgentPackageSourceStatus(
            sourceDescription: 'hobleyd/kintsugi',
            platforms: [
              AgentPackageSourceRow(
                platform: 'macos',
                availableVersion: '0.7.0',
                publishedVersion: '0.5.0',
                isNewer: true,
                newerReleases: [
                  AgentPackageReleaseNotes(version: '0.7.0', releaseNotes: 'Seventh.'),
                  AgentPackageReleaseNotes(version: '0.6.0', releaseNotes: null),
                ],
              ),
              AgentPackageSourceRow(
                platform: 'linux',
                availableVersion: '0.5.0',
                publishedVersion: '0.5.0',
                isNewer: false,
                newerReleases: [],
              ),
            ],
            unavailableReason: null,
          ),
      agentApiBaseUrl: 'https://kintsugi.example.com:8443',
      agentApiBaseUrlIsDerived: false,
      requestBaseUrl: 'https://kintsugi.example.com:8443',
      importResults: const [],
      refreshError: null,
    );

AgentPackage package(String platform) => AgentPackage(
      platform: platform,
      version: '0.5.0',
      fileName: 'kintsugi-agent-$platform-0.5.0.tar.gz',
      fileSizeBytes: 4 * 1024 * 1024,
      sha256: 'a' * 64,
      releaseNotes: 'Published notes.',
      publishedUtc: DateTime.utc(2026, 9, 1),
    );

class FakeAgentPackageRepository implements AgentPackageRepository {
  FakeAgentPackageRepository(this.current);

  ClientsView current;

  @override
  Future<ClientsView> view() async => current;

  @override
  Future<ClientsView> refresh() async => current;
}
