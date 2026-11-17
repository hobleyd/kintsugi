import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/data/models/host_mapper.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/host.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/host_usecases.dart';
import 'package:kintsugi_web/presentation/hosts/hosts_bloc.dart';
import 'package:kintsugi_web/presentation/hosts/hosts_screen.dart';

class _FakeHostRepository implements HostRepository {
  _FakeHostRepository(this.hosts);

  final List<HostSummary> hosts;

  @override
  Future<List<HostSummary>> list() async => hosts;

  @override
  Future<void> requestRemoval(String id) async {}
}

HostSummary _host(String name, String version, String state, {int status = 1, String latest = '0.12.5'}) =>
    hostFromJson({
      'id': name,
      'hostname': name,
      'serialNumber': 'SN-$name',
      'status': status,
      'agentVersion': version,
      'latestAgentVersion': latest,
      'agentUpdateState': state,
    });

/// What the server says about agent versions reaches the Hosts screen as a tag under the version, a
/// banner above the table and a filter. The rule itself — behind, per platform, and "not updating"
/// after a day — is the server's and is tested there; this pins only how it is shown.
void main() {
  group('reading the server\'s answer', () {
    test('the three states and the newest version are read', () {
      expect(_host('a', '0.12.3', 'Behind').agentUpdateState, AgentUpdateState.behind);
      expect(_host('a', '0.12.3', 'NotUpdating').agentUpdateState, AgentUpdateState.notUpdating);
      expect(_host('a', '0.12.5', 'Current').agentUpdateState, AgentUpdateState.current);
      expect(_host('a', '0.12.3', 'Behind').latestAgentVersion, '0.12.5');
    });

    test('an older server that says nothing reads as unknown, not behind', () {
      final host = hostFromJson({'id': 'a', 'hostname': 'a', 'serialNumber': 'S', 'status': 1});

      expect(host.agentUpdateState, AgentUpdateState.unknown);
      expect(host.agentUpdateState.isBehind, isFalse);
      expect(host.latestAgentVersion, isNull);
    });
  });

  group('the filter', () {
    final hosts = [
      _host('current', '0.12.5', 'Current'),
      _host('behind', '0.12.3', 'Behind'),
      _host('stuck', '0.12.3', 'NotUpdating'),
      _host('mystery', '0.1.0', 'Unknown'),
    ];

    test('all shows every host', () {
      expect(HostsState(hosts: hosts).visibleHosts, hasLength(4));
    });

    test('behind shows the behind and the stuck, and stuck only the stuck', () {
      expect(HostsState(hosts: hosts, agentFilter: 'behind').visibleHosts.map((h) => h.hostname), ['behind', 'stuck']);
      expect(HostsState(hosts: hosts, agentFilter: 'stuck').visibleHosts.map((h) => h.hostname), ['stuck']);
    });

    test('the counts ignore the filter, so choosing one never changes the number it came from', () {
      final state = HostsState(hosts: hosts, agentFilter: 'stuck');

      expect(state.behindCount, 2);
      expect(state.stuckCount, 1);
    });

    test('it combines with the search', () {
      final state = HostsState(hosts: hosts, agentFilter: 'behind', search: 'stu');

      expect(state.visibleHosts.map((h) => h.hostname), ['stuck']);
    });
  });

  group('the screen', () {
    Future<void> pumpWith(WidgetTester tester, List<HostSummary> hosts) async {
      locator
        ..registerSingleton(GetHosts(_FakeHostRepository(hosts)))
        ..registerSingleton(RequestHostRemoval(_FakeHostRepository(const [])));

      tester.view.physicalSize = const Size(1500, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);

      await tester.pumpWidget(
        MaterialApp(theme: AppTheme.light(), home: const Scaffold(body: HostsScreen())),
      );
      await tester.pump();
    }

    Future<void> tearDownScreen(WidgetTester tester) => tester.pumpWidget(const SizedBox());

    tearDown(() => locator.reset());

    testWidgets('a host that is behind carries a tag with the latest version; a current one has none', (tester) async {
      await pumpWith(tester, [_host('old', '0.12.3', 'Behind'), _host('fresh', '0.12.5', 'Current')]);

      expect(find.text('BEHIND · 0.12.5'), findsOneWidget);
      expect(find.textContaining('BEHIND'), findsOneWidget, reason: 'only the host that is behind');

      await tearDownScreen(tester);
    });

    testWidgets('a host that looks stuck says it is not updating, and the banner turns to it', (tester) async {
      await pumpWith(tester, [_host('stuck', '0.12.3', 'NotUpdating')]);

      expect(find.text('NOT UPDATING · 0.12.5'), findsOneWidget);
      expect(find.textContaining('automatic update is probably failing'), findsOneWidget);

      await tearDownScreen(tester);
    });

    testWidgets('when every host is current there is no banner at all', (tester) async {
      await pumpWith(tester, [_host('fresh', '0.12.5', 'Current')]);

      expect(find.textContaining('behind the latest agent'), findsNothing);
      expect(find.text('SHOW THEM'), findsNothing);

      await tearDownScreen(tester);
    });

    testWidgets('Show them filters to the hosts behind', (tester) async {
      await pumpWith(tester, [_host('old', '0.12.3', 'Behind'), _host('fresh', '0.12.5', 'Current')]);
      expect(find.text('fresh'), findsOneWidget);

      await tester.tap(find.text('SHOW THEM'));
      await tester.pump();

      expect(find.text('old'), findsOneWidget);
      expect(find.text('fresh'), findsNothing);

      await tearDownScreen(tester);
    });
  });
}
