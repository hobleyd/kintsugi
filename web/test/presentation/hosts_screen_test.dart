import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/status_chip.dart';
import 'package:kintsugi_web/data/models/host_mapper.dart';
import 'package:kintsugi_web/domain/entities/host.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/host_usecases.dart';
import 'package:kintsugi_web/presentation/hosts/hosts_screen.dart';

class _FakeHostRepository implements HostRepository {
  _FakeHostRepository(this.hosts);

  final List<HostSummary> hosts;

  @override
  Future<List<HostSummary>> list() async => hosts;

  @override
  Future<void> requestRemoval(String id) async {}
}

/// The Status column: the status chip, with the agent's version in brackets on the line beneath.
///
/// Pumps the real [HostsScreen] against a fake registered in [locator] the way `injection.dart`
/// registers the real one. The version is the one thing here without a server-side mirror test:
/// `HostDto.AgentVersion` reaches the screen through `hostFromJson`, and where it lands is decided
/// only by this widget.
void main() {
  setUp(() {
    locator
      ..registerSingleton(GetHosts(_FakeHostRepository([
        hostFromJson({'id': 'a', 'hostname': 'alpha', 'serialNumber': 'A1', 'status': 1, 'agentVersion': '0.6.1'}),
        hostFromJson({'id': 'b', 'hostname': 'bravo', 'serialNumber': 'B2', 'status': 2}),
      ])))
      ..registerSingleton(RequestHostRemoval(_FakeHostRepository(const [])));
  });

  tearDown(() => locator.reset());

  Future<void> pumpScreen(WidgetTester tester) async {
    tester.view.physicalSize = const Size(1400, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        home: const Scaffold(body: HostsScreen()),
      ),
    );
    await tester.pump();
  }

  /// Disposes the screen so its polling bloc closes before the test's fake clock is checked for
  /// timers still pending.
  Future<void> tearDownScreen(WidgetTester tester) => tester.pumpWidget(const SizedBox());

  testWidgets('a host that reported its agent version shows it under the status chip', (tester) async {
    await pumpScreen(tester);

    final chip = tester.getRect(find.ancestor(of: find.text('ONLINE'), matching: find.byType(StatusChip)));
    final version = tester.getRect(find.text('(0.6.1)'));
    expect(version.top, greaterThanOrEqualTo(chip.bottom));
    expect(version.left, chip.left);

    // The host whose agent predates the field gets the chip alone — no empty brackets.
    expect(find.text('OFFLINE'), findsOneWidget);
    expect(find.text('()'), findsNothing);
    expect(find.textContaining(RegExp(r'^\(.*\)$')), findsOneWidget);

    await tearDownScreen(tester);
  });
}
