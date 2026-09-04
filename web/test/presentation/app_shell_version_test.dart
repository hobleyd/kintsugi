import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/theme/theme_cubit.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/server_info_usecases.dart';
import 'package:kintsugi_web/presentation/shell/app_shell.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'session_bloc_test.dart' show FakeSessionRepository, blocFor, readySession;

class _FakeServerInfoRepository implements ServerInfoRepository {
  final completer = Completer<String>();

  @override
  Future<String> version() => completer.future;
}

/// The server's version under the Kintsugi name in the sidebar.
///
/// Pumps the real [AppShell] against fakes registered in [locator] the way `injection.dart`
/// registers the real ones. What this pins is the surface: the brand block renders without the
/// version until the route answers, and shows it beneath the name once it has.
void main() {
  setUp(() => SharedPreferences.setMockInitialValues({}));

  tearDown(() => locator.reset());

  /// Registered from the test body rather than `setUp`: the completer has to be created inside
  /// `testWidgets`' fake-async zone, or its completion is queued where `pump` never flushes it.
  Future<_FakeServerInfoRepository> pumpShell(WidgetTester tester) async {
    final server = _FakeServerInfoRepository();
    locator.registerSingleton(GetServerVersion(server));

    tester.view.physicalSize = const Size(1400, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    final preferences = await SharedPreferences.getInstance();
    await tester.pumpWidget(
      MultiBlocProvider(
        providers: [
          BlocProvider.value(value: blocFor(FakeSessionRepository(session: readySession()))),
          BlocProvider(create: (_) => ThemeCubit(preferences)),
        ],
        child: MaterialApp(
          theme: AppTheme.light(),
          home: const AppShell(location: '/hosts', child: SizedBox()),
        ),
      ),
    );
    await tester.pump();
    return server;
  }

  testWidgets('shows the server version under the brand once it arrives', (tester) async {
    final server = await pumpShell(tester);

    expect(find.text('KINTSUGI'), findsOneWidget);
    expect(find.textContaining('v1.0.0'), findsNothing);

    server.completer.complete('1.0.0');
    await tester.pumpAndSettle();

    final brand = tester.getCenter(find.text('KINTSUGI'));
    final version = tester.getCenter(find.text('v1.0.0'));
    expect(version.dy, greaterThan(brand.dy));
    expect((version.dx - brand.dx).abs(), lessThan(1));
  });
}
