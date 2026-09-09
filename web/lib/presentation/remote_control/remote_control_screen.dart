import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';

import '../../core/di/locator.dart';
import '../../core/platform/full_screen.dart';
import '../../core/router/app_router.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/remote_control_session.dart';
import '../../domain/usecases/remote_control_usecases.dart';
import 'remote_control_bloc.dart';
import 'remote_screen_view.dart';
import 'remote_shell_view.dart';

/// One host's remote session — its screen, or a terminal on it.
///
/// Both kinds are this screen because they are the same session underneath: the same request, the
/// same poll, the same relayed socket, the same audit row. What differs is what the panel holds and
/// whether anybody at the host was asked.
///
/// A **screen** session starts by *asking*: the host's own user gets a dialog naming the
/// administrator, and nothing is captured or shown here until they allow it, so everything before
/// that point is a waiting state. A **shell** session asks nobody and opens as soon as the agent
/// answers — see `RemoteControlSessionKind` on the server for why, and what stands in for the
/// dialog.
class RemoteControlScreen extends StatefulWidget {
  const RemoteControlScreen({
    required this.hostId,
    this.kind = RemoteControlSessionKind.screen,
    this.hostname,
    this.fullScreen,
    super.key,
  });

  final String hostId;

  /// What to open. Fixed by the route rather than chosen here, so the two are separate addresses a
  /// support call can be pointed at.
  final RemoteControlSessionKind kind;

  /// Passed through from the Hosts screen so the heading names the host immediately, before the
  /// first response has arrived. Absent on a bookmarked or hand-typed URL, which is why the screen
  /// falls back to the session's own copy.
  final String? hostname;

  /// The browser's full-screen mode, or null in a test that has not registered one.
  ///
  /// Nullable rather than required so this screen can be pumped without a DOM — `package:web` is
  /// unavailable under `flutter test`, where `kIsWeb` is false. Null means the toggle is not
  /// offered and nothing is requested.
  final FullScreenController? fullScreen;

  @override
  State<RemoteControlScreen> createState() => _RemoteControlScreenState();
}

class _RemoteControlScreenState extends State<RemoteControlScreen> {
  StreamSubscription<bool>? _fullScreenChanges;
  bool _isFullScreen = false;

  /// Set when the browser refused a full-screen request, so the screen can offer the button rather
  /// than silently doing nothing. It refuses for one ordinary reason — see [initState].
  bool _fullScreenRefused = false;

  @override
  void initState() {
    super.initState();

    final fullScreen = widget.fullScreen;
    if (fullScreen == null) return;

    _isFullScreen = fullScreen.isFullScreen;
    _fullScreenChanges = fullScreen.onChanged.listen((isFullScreen) {
      if (mounted) setState(() => _isFullScreen = isFullScreen);
    });

    // **Requested here, synchronously off the click that navigated here, and that timing is the
    // whole of it.** `requestFullscreen` needs transient user activation — about five seconds after
    // a gesture in Chrome — and `initState` runs on the same turn as the Connect press that pushed
    // this route. Asking any later would be asking after the *host user's* consent dialog, which is
    // a person clicking something up to sixty seconds away, and would be refused every time.
    //
    // Deliberately not awaited before the first build: a refusal is ordinary rather than
    // exceptional (a bookmarked URL opened by pressing Enter has no gesture behind it), and the
    // answer only decides whether the toggle below says "Full screen" or "Exit full screen".
    if (!_isFullScreen) {
      fullScreen.enter().then((entered) {
        if (mounted && !entered) setState(() => _fullScreenRefused = true);
      });
    }
  }

  @override
  void dispose() {
    _fullScreenChanges?.cancel();
    // Left on the way out rather than when the session ends: a page that stayed full-screen after
    // navigating back to Hosts would show that screen with no sidebar and no way to guess why.
    widget.fullScreen?.exit();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => RemoteControlBloc(
          requestSession: locator<RequestRemoteControlSession>(),
          getSession: locator<GetRemoteControlSession>(),
          endSession: locator<EndRemoteControlSession>(),
          openStream: locator<OpenRemoteControlStream>(),
        )..add(RemoteControlRequested(widget.hostId, widget.kind)),
        child: _RemoteControlView(
          kind: widget.kind,
          hostname: widget.hostname,
          isFullScreen: _isFullScreen,
          // Hidden entirely when there is no controller — a button that cannot do anything is worse
          // than no button. Offered whenever there is one, including after a refusal, because the
          // press itself is the gesture the browser was waiting for.
          onToggleFullScreen: widget.fullScreen == null
              ? null
              : () async {
                  final fullScreen = widget.fullScreen!;
                  if (_isFullScreen) {
                    await fullScreen.exit();
                  } else if (!await fullScreen.enter() && mounted) {
                    setState(() => _fullScreenRefused = true);
                  }
                },
          fullScreenRefused: _fullScreenRefused,
        ),
      );
}

class _RemoteControlView extends StatelessWidget {
  const _RemoteControlView({
    required this.kind,
    required this.isFullScreen,
    required this.fullScreenRefused,
    this.hostname,
    this.onToggleFullScreen,
  });

  final RemoteControlSessionKind kind;
  final String? hostname;
  final bool isFullScreen;
  final bool fullScreenRefused;
  final Future<void> Function()? onToggleFullScreen;

  @override
  Widget build(BuildContext context) => BlocBuilder<RemoteControlBloc, RemoteControlState>(
        builder: (context, state) {
          final session = state.session;
          final name = session?.hostname.isNotEmpty == true ? session!.hostname : (hostname ?? 'this host');

          final shell = state.shell;

          final subtitle = session == null
              ? 'Connecting to $name'
              // The account is named the moment it is known: root on macOS and Linux, SYSTEM on
              // Windows, never the logged-in user. Shown rather than assumed because a shell
              // running as somebody's own account would be a different thing to hand out, and an
              // administrator should be able to see which they have.
              : shell != null
                  ? '$name — ${shell.shell} as ${shell.user}, requested by ${session.requestedBy}'
                  : '$name — requested by ${session.requestedBy}';

          // **Full screen drops the page chrome as well as the sidebar, and that is the point of
          // it.** `PageScaffold`'s heading, the panel's inset and the shell's 240px of navigation
          // together cost a third of the width on a laptop — width that is the host's screen. The
          // action row stays, because it is where Disconnect and the display picker live and a
          // session with no way out of it would be worse than a small one.
          if (isFullScreen) {
            return _FullScreenFrame(
              subtitle: subtitle,
              actions: _buildActions(context, state),
              notice: state.error != null ? AlertBox.error(state.error!) : _buildConsentNotice(state),
              body: _buildBody(context, state),
            );
          }

          return PageScaffold(
            title: kind == RemoteControlSessionKind.shell ? 'Remote Terminal' : 'Remote Control',
            subtitle: subtitle,
            children: [
              if (state.error != null) AlertBox.error(state.error!),
              _buildConsentNotice(state),
              KintsugiPanel(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    _buildActions(context, state),
                    const SizedBox(height: 12),
                    _buildBody(context, state),
                  ],
                ),
              ),
            ],
          );
        },
      );

  /// The consent state, said explicitly rather than left implicit in an empty screen.
  ///
  /// Each of these is something the administrator has to act on — wait, give up, or go and speak to
  /// somebody — so none of them should look like a loading spinner.
  Widget _buildConsentNotice(RemoteControlState state) {
    final session = state.session;
    if (session == null) return const SizedBox.shrink();

    // Said plainly rather than left to be assumed from a terminal that simply opened. Nobody at this
    // host was asked, and an administrator should know that is what they are doing — the record of
    // who opened it is the only thing standing in for the dialog a screen session shows.
    //
    // Deliberately says nothing about whether the host shows the session, because the three agents
    // differ: the macOS one puts it in the menu bar (`tray_menu::report_remote_session`), and the
    // Linux and Windows shells run outside any desktop and announce nothing at all. A sentence
    // claiming either would be false on some of the fleet, and the honesty of this notice is the
    // whole reason it exists.
    if (session.kind == RemoteControlSessionKind.shell && session.isConnectable) {
      return AlertBox.info(
        'Nobody at ${session.hostname} was asked before this terminal opened. The session has been '
        'recorded against your name.',
      );
    }

    // A shell the agent has not answered yet gets no notice at all. It is Pending only in the
    // window between the request and the agent reporting NotRequired — waiting for the *agent*,
    // never for a person — and both sentences available below claim somebody is looking at a dialog
    // that was never raised. The body says it is connecting instead, and the notice above takes over
    // the moment the answer arrives. Deliberately narrow: an unreachable or unavailable shell still
    // falls through to the switch, which is where those are said out loud.
    if (session.kind == RemoteControlSessionKind.shell &&
        session.consent == RemoteControlConsent.pending &&
        session.endedAtUtc == null) {
      return const SizedBox.shrink();
    }

    if (session.isAwaitingConsent) {
      return AlertBox.info(
        'Waiting for the person at ${session.hostname} to allow this. They have been shown a dialog '
        'naming you; nothing is captured until they agree.',
      );
    }

    return switch (session.consent) {
      _ when session.endedAtUtc != null =>
        AlertBox.info('The session ended: ${session.endReason ?? 'the connection closed'}.'),
      final consent when !session.isConnectable => AlertBox.info(consent.label),
      _ => const SizedBox.shrink(),
    };
  }

  Widget _buildActions(BuildContext context, RemoteControlState state) {
    final bloc = context.read<RemoteControlBloc>();
    final session = state.session;
    final live = session != null && session.endedAtUtc == null;

    final geometry = state.geometry;

    return Wrap(
      spacing: 8,
      runSpacing: 8,
      crossAxisAlignment: WrapCrossAlignment.center,
      children: [
        // Only where there is a genuine choice: one display is not one, and a picker naming the
        // single screen a laptop has is a control that can only be set to where it already is.
        if (state.isStreaming && geometry != null && geometry.hasDisplayChoice)
          _DisplayPicker(
            displays: geometry.displays,
            activeDisplayId: geometry.activeDisplayId,
            onChanged: (id) => bloc.add(RemoteControlInputSent(RemoteDisplaySelection(id))),
          ),
        if (state.isStreaming && state.geometry != null)
          _QualityPicker(
            // Keyed on the session, so a second session on this screen does not show the last
            // one's setting beside an agent that has gone back to its own default.
            key: ValueKey(session?.id),
            onChanged: (quality) => bloc.add(
              RemoteControlInputSent(RemoteQualityInput(jpegQuality: quality)),
            ),
          ),
        if (onToggleFullScreen != null)
          SecondaryButton(
            label: isFullScreen ? 'Exit Full Screen' : 'Full Screen',
            onPressed: () => onToggleFullScreen!(),
          ),
        if (live)
          SecondaryButton(
            label: 'Disconnect',
            onPressed: () => bloc.add(const RemoteControlDisconnectRequested()),
          ),
        SecondaryButton(
          label: 'Back to Hosts',
          onPressed: () {
            // Ends the session on the way out rather than leaving it running because a tab
            // navigated away. The agent would notice the socket closing eventually, but "eventually"
            // is not good enough for something that is capturing somebody's screen.
            if (live) bloc.add(const RemoteControlDisconnectRequested());
            context.go(Routes.hosts);
          },
        ),
      ],
    );
  }

  Widget _buildBody(BuildContext context, RemoteControlState state) {
    final bloc = context.read<RemoteControlBloc>();

    if (state.shell != null && state.isStreaming) {
      return RemoteShellView(
        // Keyed on the session, so a second session on this screen gets a fresh terminal rather
        // than the last one's scrollback and its stale cursor position.
        key: ValueKey(state.session?.id),
        output: bloc.shellOutput,
        onInput: (input) => bloc.add(RemoteControlInputSent(input)),
      );
    }

    final geometry = state.geometry;

    if (geometry == null || !state.isStreaming) {
      return Padding(
        padding: const EdgeInsets.symmetric(vertical: 48),
        child: Center(
          child: Text(
            state.status ?? 'Nothing to show yet.',
            textAlign: TextAlign.center,
            style: Theme.of(context).textTheme.bodyMedium,
          ),
        ),
      );
    }

    return RemoteScreenView(
      geometry: geometry,
      tiles: state.tiles,
      onInput: (input) => context.read<RemoteControlBloc>().add(RemoteControlInputSent(input)),
    );
  }
}

/// The page as it looks with the browser in full screen: the session, and nothing else.
///
/// A bare column rather than [PageScaffold] because the whole reason to be full-screen is the width
/// and height the heading, the panel inset and the sidebar were taking. The subtitle survives as one
/// small line, since "who asked, and what account this shell runs as" is the thing an administrator
/// should not have to leave full screen to check.
class _FullScreenFrame extends StatelessWidget {
  const _FullScreenFrame({
    required this.subtitle,
    required this.actions,
    required this.notice,
    required this.body,
  });

  final String subtitle;
  final Widget actions;
  final Widget notice;
  final Widget body;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    subtitle,
                    style: Theme.of(context).textTheme.bodySmall,
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),
            actions,
            notice,
            const SizedBox(height: 8),
            // The session takes everything left. `Expanded` rather than letting the column size to
            // its child: the screen view is an `AspectRatio` inside a `Center`, so unbounded height
            // would leave it at whatever width it happened to get and the reclaimed space unused.
            Expanded(child: SingleChildScrollView(child: body)),
          ],
        ),
      );
}

/// Which of the host's displays to watch.
///
/// **Shown only when the host offered more than one**, which is why this widget never has to render
/// an empty or single-entry state — see `RemoteDisplayGeometry.hasDisplayChoice`.
///
/// Stateless, unlike the quality picker beside it: the agent is the authority on which display is
/// being shown, and it says so in every geometry message. Holding a local selection would let the
/// dropdown claim a display the switch had failed to reach — which is a real case, since a monitor
/// can be unplugged between the list being drawn and the choice being made, and the agent then
/// stays where it was and says so.
class _DisplayPicker extends StatelessWidget {
  const _DisplayPicker({
    required this.displays,
    required this.activeDisplayId,
    required this.onChanged,
  });

  final List<RemoteDisplayOption> displays;
  final int activeDisplayId;
  final ValueChanged<int> onChanged;

  @override
  Widget build(BuildContext context) {
    // A value the list does not contain would assert inside DropdownButton. It happens legitimately
    // in the moment before the first geometry of a switched-to display arrives on Wayland, where the
    // active id follows the *frames* rather than the request.
    final value = displays.any((display) => display.id == activeDisplayId) ? activeDisplayId : null;

    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        const Text('Display'),
        const SizedBox(width: 8),
        DropdownButton<int>(
          value: value,
          hint: const Text('Switching…'),
          onChanged: (id) {
            if (id == null || id == activeDisplayId) return;
            onChanged(id);
          },
          items: [
            for (final display in displays)
              DropdownMenuItem(
                value: display.id,
                child: Text(display.isPrimary ? '${display.label} — primary' : display.label),
              ),
          ],
        ),
      ],
    );
  }
}

/// How much picture to spend bandwidth on.
///
/// Worth having rather than a fixed quality: the same session is run over an office LAN and over a
/// phone tether, and the useful setting is different by an order of magnitude. Changing it makes the
/// agent resend the whole screen, so the change is visible immediately.
class _QualityPicker extends StatefulWidget {
  const _QualityPicker({required this.onChanged, super.key});

  final ValueChanged<int> onChanged;

  @override
  State<_QualityPicker> createState() => _QualityPickerState();
}

class _QualityPickerState extends State<_QualityPicker> {
  int _quality = 60;

  @override
  Widget build(BuildContext context) => Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          const Text('Quality'),
          const SizedBox(width: 8),
          DropdownButton<int>(
            value: _quality,
            onChanged: (quality) {
              if (quality == null) return;
              setState(() => _quality = quality);
              widget.onChanged(quality);
            },
            items: const [
              DropdownMenuItem(value: 30, child: Text('Low')),
              DropdownMenuItem(value: 60, child: Text('Normal')),
              DropdownMenuItem(value: 85, child: Text('High')),
            ],
          ),
        ],
      );
}
