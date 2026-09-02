import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/text_bits.dart';
import 'session_bloc.dart';

/// Shown while `GET /api/session` is in flight — the one moment where the right screen genuinely
/// is not yet known.
class StartingScreen extends StatelessWidget {
  const StartingScreen({super.key});

  @override
  Widget build(BuildContext context) => Scaffold(
        body: Center(
          child: SizedBox(
            width: 28,
            height: 28,
            child: CircularProgressIndicator(strokeWidth: 2, color: context.palette.neon),
          ),
        ),
      );
}

/// Shown when the bootstrap call itself failed.
///
/// Distinct from the sign-in screen on purpose: "not signed in" is a successful answer, whereas
/// this means the API is unreachable or something in front of it is answering instead. Offering a
/// sign-in button here would send someone round a loop that cannot complete.
class ServerUnavailableScreen extends StatelessWidget {
  const ServerUnavailableScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final session = context.watch<SessionBloc>().state;
    final message = session is SessionUnavailable ? session.message : 'The server could not be reached.';

    return Scaffold(
      body: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 460),
          child: KintsugiPanel(
            padding: const EdgeInsets.all(40),
            child: Column(
              children: [
                Text(
                  'CANNOT REACH KINTSUGI',
                  textAlign: TextAlign.center,
                  style: Theme.of(context).textTheme.titleLarge,
                ),
                const SizedBox(height: 16),
                HintText(message, textAlign: TextAlign.center),
                const SizedBox(height: 24),
                PrimaryButton(
                  label: 'Try again',
                  onPressed: () => context.read<SessionBloc>().add(const SessionRequested()),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
