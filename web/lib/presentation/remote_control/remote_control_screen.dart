import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';

import '../../core/di/injection.dart';
import '../../core/router/app_router.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../domain/entities/remote_control_session.dart';
import '../../domain/usecases/remote_control_usecases.dart';
import 'remote_control_bloc.dart';
import 'remote_screen_view.dart';

/// Controlling one host's screen.
///
/// Reached from the Hosts screen's Connect action, and it starts by *asking*: the host's own user
/// gets a dialog naming the administrator, and nothing is captured or shown here until they allow
/// it. Everything on this screen before that point is a waiting state.
class RemoteControlScreen extends StatelessWidget {
  const RemoteControlScreen({required this.hostId, this.hostname, super.key});

  final String hostId;

  /// Passed through from the Hosts screen so the heading names the host immediately, before the
  /// first response has arrived. Absent on a bookmarked or hand-typed URL, which is why the screen
  /// falls back to the session's own copy.
  final String? hostname;

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => RemoteControlBloc(
          requestSession: locator<RequestRemoteControlSession>(),
          getSession: locator<GetRemoteControlSession>(),
          endSession: locator<EndRemoteControlSession>(),
          openStream: locator<OpenRemoteControlStream>(),
        )..add(RemoteControlRequested(hostId)),
        child: _RemoteControlView(hostname: hostname),
      );
}

class _RemoteControlView extends StatelessWidget {
  const _RemoteControlView({this.hostname});

  final String? hostname;

  @override
  Widget build(BuildContext context) => BlocBuilder<RemoteControlBloc, RemoteControlState>(
        builder: (context, state) {
          final session = state.session;
          final name = session?.hostname.isNotEmpty == true ? session!.hostname : (hostname ?? 'this host');

          return PageScaffold(
            title: 'Remote Control',
            subtitle: session == null
                ? 'Connecting to $name'
                : '$name — requested by ${session.requestedBy}',
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

    return Wrap(
      spacing: 8,
      runSpacing: 8,
      crossAxisAlignment: WrapCrossAlignment.center,
      children: [
        if (state.isStreaming)
          _QualityPicker(
            // Keyed on the session, so a second session on this screen does not show the last
            // one's setting beside an agent that has gone back to its own default.
            key: ValueKey(session?.id),
            onChanged: (quality) => bloc.add(
              RemoteControlInputSent(RemoteQualityInput(jpegQuality: quality)),
            ),
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
