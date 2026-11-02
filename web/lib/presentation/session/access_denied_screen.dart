import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/widgets/buttons.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/text_bits.dart';
import 'session_bloc.dart';

/// Shown to someone who signed in successfully but whose account is not on the administrators
/// list. See `Session.isDenied`.
///
/// Not the sign-in screen: signing in again would land here again. The one useful action is to
/// sign out, so that someone who picked the wrong account can choose another.
class AccessDeniedScreen extends StatelessWidget {
  const AccessDeniedScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final state = context.watch<SessionBloc>().state;
    // The full email when there is one: the commonest reason to land here is the wrong account
    // (jake@gmail.com rather than jake@htw.com.au), which only the whole address shows.
    final user = state is SessionReady ? state.session.email ?? state.session.userName : null;

    return Scaffold(
      body: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 460),
          child: KintsugiPanel(
            padding: const EdgeInsets.all(40),
            child: Column(
              children: [
                Text(
                  'ACCESS DENIED',
                  textAlign: TextAlign.center,
                  style: Theme.of(context).textTheme.titleLarge,
                ),
                const SizedBox(height: 16),
                HintText(
                  user == null
                      ? 'This account is not an administrator of Kintsugi.'
                      : '$user is not an administrator of Kintsugi.',
                  textAlign: TextAlign.center,
                ),
                const SizedBox(height: 8),
                const HintText(
                  'Ask an existing administrator to add your email address, or sign out and sign '
                  'in with a different account.',
                  textAlign: TextAlign.center,
                ),
                const SizedBox(height: 24),
                SizedBox(
                  width: double.infinity,
                  child: PrimaryButton(
                    label: 'Sign out',
                    onPressed: () => context.read<SessionBloc>().add(const SignOutRequested()),
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
