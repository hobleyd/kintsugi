import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/platform/full_screen.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/remote_control_session.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/remote_control_usecases.dart';
import 'package:kintsugi_web/presentation/remote_control/remote_control_screen.dart';

// Aliased rather than shown: `_FakeRepository` has its own `session` method, and an unaliased
// import would be shadowed by it inside the class.
import 'remote_control_bloc_test.dart' as fakes;

/// A media channel this test drives by hand, recording what the screen sends back.
class _FakeStream implements RemoteControlStream {
  final _updates = StreamController<RemoteScreenUpdate>.broadcast();
  final sent = <RemoteInput>[];

  @override
  Stream<RemoteScreenUpdate> get updates => _updates.stream;

  @override
  void send(RemoteInput input) => sent.add(input);

  @override
  Future<void> close() => _updates.close();

  void emit(RemoteScreenUpdate update) => _updates.add(update);
}

class _FakeRepository implements RemoteControlRepository {
  _FakeRepository(this.stream);

  final _FakeStream stream;

  @override
  Future<RemoteControlSession> request(String hostId, RemoteControlSessionKind kind) async => granted;

  @override
  Future<RemoteControlSession?> session(String id) async => granted;

  @override
  Future<void> end(String id) async {}

  @override
  RemoteControlStream openStream(String sessionId) => stream;
}

final granted = fakes.session(
  kind: RemoteControlSessionKind.screen,
  consent: RemoteControlConsent.granted,
);

/// A [FullScreenController] that records what was asked of it and can refuse, which is what a
/// browser does to a request made outside its activation window.
class _FakeFullScreen implements FullScreenController {
  _FakeFullScreen({this.agrees = true});

  final bool agrees;
  final _changes = StreamController<bool>.broadcast();

  bool _isFullScreen = false;
  int enterCalls = 0;
  int exitCalls = 0;

  @override
  bool get isFullScreen => _isFullScreen;

  @override
  Future<bool> enter() async {
    enterCalls++;
    if (!agrees) return false;
    _isFullScreen = true;
    _changes.add(true);
    return true;
  }

  @override
  Future<void> exit() async {
    exitCalls++;
    _isFullScreen = false;
    _changes.add(false);
  }

  @override
  Stream<bool> get onChanged => _changes.stream;
}

const _twoDisplays = RemoteDisplayGeometry(
  pointWidth: 1920,
  pointHeight: 1080,
  imageWidth: 1600,
  imageHeight: 900,
  activeDisplayId: 1,
  displays: [
    RemoteDisplayOption(id: 1, label: 'Display 1 (1920 x 1080)', width: 1920, height: 1080, isPrimary: true),
    RemoteDisplayOption(id: 2, label: 'Display 2 (1920 x 1080)', width: 1920, height: 1080, isPrimary: false),
  ],
);

const _oneDisplay = RemoteDisplayGeometry(
  pointWidth: 1920,
  pointHeight: 1080,
  imageWidth: 1600,
  imageHeight: 900,
  activeDisplayId: 1,
  displays: [
    RemoteDisplayOption(id: 1, label: 'Built-in Display (1920 x 1080)', width: 1920, height: 1080, isPrimary: true),
  ],
);

/// The viewer's two additions: choosing which of a host's displays to watch, and taking the whole
/// browser window while doing it.
///
/// Worth a widget test rather than only a bloc one, because both of the things that go wrong here
/// are in the wiring. The picker must not appear for a host with one display — a control that can
/// only be set to where it already is. And full screen has to be *requested in `initState`*, on the
/// same turn as the click that navigated here: `requestFullscreen` needs transient user activation,
/// so a request made after the host user's consent arrives — up to sixty seconds later — is refused
/// every time.
void main() {
  Future<(_FakeStream, _FakeFullScreen)> pump(
    WidgetTester tester, {
    bool fullScreenAgrees = true,
  }) async {
    final stream = _FakeStream();
    final repository = _FakeRepository(stream);
    final fullScreen = _FakeFullScreen(agrees: fullScreenAgrees);

    locator
      ..registerSingleton(RequestRemoteControlSession(repository))
      ..registerSingleton(GetRemoteControlSession(repository))
      ..registerSingleton(EndRemoteControlSession(repository))
      ..registerSingleton(OpenRemoteControlStream(repository));

    tester.view.physicalSize = const Size(1600, 1000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    addTearDown(locator.reset);

    // Inside a Scaffold because `AppShell` is one, and the dropdowns on this screen need a
    // `Material` ancestor — pumping the screen bare would fail on that rather than on anything this
    // test is about.
    await tester.pumpWidget(MaterialApp(
      theme: AppTheme.light(),
      home: Scaffold(
        body: RemoteControlScreen(hostId: 'host', hostname: 'mac-01', fullScreen: fullScreen),
      ),
    ));
    await tester.pump();

    return (stream, fullScreen);
  }

  /// One test against a freshly pumped screen, torn down before the body returns.
  ///
  /// The teardown is inside the body rather than in `addTearDown` deliberately: the screen's bloc
  /// polls every two seconds, and the framework's "a Timer is still pending" assertion fires at the
  /// end of the body — before any registered teardown runs. Replacing the tree disposes the
  /// BlocProvider, which closes the bloc and stops the poll, which is exactly what navigating away
  /// from this screen does in the app.
  void screenTest(
    String description,
    Future<void> Function(WidgetTester tester, _FakeStream stream, _FakeFullScreen fullScreen) run, {
    bool fullScreenAgrees = true,
  }) {
    testWidgets(description, (tester) async {
      final (stream, fullScreen) = await pump(tester, fullScreenAgrees: fullScreenAgrees);
      await run(tester, stream, fullScreen);
      await tester.pumpWidget(const SizedBox());
    });
  }

  screenTest('offers no display picker for a host with one display', (tester, stream, _) async {
    stream.emit(_oneDisplay);
    await tester.pump();

    expect(find.text('Display'), findsNothing);
  });

  screenTest('offers the picker once the host reports two, naming the primary', (tester, stream, _) async {
    stream.emit(_twoDisplays);
    await tester.pump();

    expect(find.text('Display'), findsOneWidget);
    // The label the agent built, not one composed here — two identical monitors are told apart by
    // what the host calls them and by which is primary, and nothing else.
    expect(find.text('Display 1 (1920 x 1080) — primary'), findsWidgets);
  });

  screenTest('sends the display the administrator picked, and nothing before that', (tester, stream, _) async {
    stream.emit(_twoDisplays);
    await tester.pump();
    expect(stream.sent, isEmpty);

    await tester.tap(find.text('Display 1 (1920 x 1080) — primary').first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Display 2 (1920 x 1080)').last);
    await tester.pumpAndSettle();

    // The id the agent offered, echoed back unchanged: it is opaque to this client, and reading
    // anything into it is what a mapping table between the two ends would be.
    expect(stream.sent, [isA<RemoteDisplaySelection>().having((i) => i.displayId, 'displayId', 2)]);
  });

  screenTest('keeps showing the display the agent says it is on, not the one that was clicked',
      (tester, stream, _) async {
    // **A local selection would be a lie on Wayland**, where a switch is asynchronous: the agent
    // renegotiates a portal stream and only then reports the new display. Until it does, the dropdown
    // has to say it is switching rather than claim a display that may never be reached — a monitor
    // can be unplugged between the list being drawn and the choice being made, and the agent then
    // stays where it was.
    stream.emit(_twoDisplays);
    await tester.pump();

    await tester.tap(find.text('Display 1 (1920 x 1080) — primary').first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Display 2 (1920 x 1080)').last);
    await tester.pumpAndSettle();

    // Still on display 1 as far as the agent has said, so that is what is shown.
    expect(find.text('Display 1 (1920 x 1080) — primary'), findsWidgets);
  });

  screenTest('asks for full screen as it opens, and gives it back on the way out',
      (tester, _, fullScreen) async {
    expect(fullScreen.enterCalls, 1, reason: 'the request has to ride the click that navigated here');
    await tester.pumpAndSettle();
    expect(fullScreen.isFullScreen, isTrue);

    // Navigating away, which is what "Back to Hosts" and the browser's own back button both do.
    await tester.pumpWidget(
      MaterialApp(theme: AppTheme.light(), home: const Scaffold(body: SizedBox())),
    );
    await tester.pump();

    expect(fullScreen.exitCalls, greaterThanOrEqualTo(1));
  });

  screenTest('drops the page heading in full screen but keeps a way out of the session',
      (tester, stream, _) async {
    await tester.pumpAndSettle();

    stream.emit(_twoDisplays);
    await tester.pump();

    // The heading is the width full screen exists to reclaim; Disconnect is the one control a
    // session must never be without.
    // PageScaffold upper-cases its title, so this is the heading as it is actually painted.
    expect(find.text('REMOTE CONTROL'), findsNothing);
    // SecondaryButton upper-cases its label, so these are the buttons as painted.
    expect(find.text('DISCONNECT'), findsOneWidget);
    expect(find.text('EXIT FULL SCREEN'), findsOneWidget);
  });

  screenTest('offers the button rather than an error when the browser refuses',
      (tester, _, fullScreen) async {
    // A bookmarked URL opened by pressing Enter has no gesture behind it, so the request is declined.
    // That is ordinary: the page stays as it was and the toggle is there to press, which *is* the
    // gesture the browser was waiting for.
    await tester.pumpAndSettle();

    expect(fullScreen.isFullScreen, isFalse);
    expect(find.text('REMOTE CONTROL'), findsOneWidget);
    expect(find.text('FULL SCREEN'), findsOneWidget);
  }, fullScreenAgrees: false);
}
