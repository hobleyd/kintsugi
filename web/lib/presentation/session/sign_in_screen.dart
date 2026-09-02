import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/widgets/buttons.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/text_bits.dart';
import 'session_bloc.dart';

/// The sign-in screen. What `Pages/Account/Login.cshtml` was.
///
/// The button is a whole-page navigation to `GET /api/auth/challenge`, not a request: the server
/// holds the client secret and performs the code exchange, and what comes back from the provider
/// is a redirect chain this app cannot follow from inside a `fetch`.
class SignInScreen extends StatelessWidget {
  const SignInScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final state = context.watch<SessionBloc>().state;
    final session = state is SessionReady ? state.session : null;
    final provider = session?.providerDisplayName ?? 'your identity provider';

    return Scaffold(
      body: Center(
        child: ConstrainedBox(
          // 460, not the 380 this started at: three of the four provider names overflowed, and
          // "single sign-on" broke worst of all — a line break after the hyphen.
          constraints: const BoxConstraints(maxWidth: 460),
          child: KintsugiPanel(
            padding: const EdgeInsets.all(40),
            child: Column(
              children: [
                ClipRRect(
                  borderRadius: BorderRadius.circular(10),
                  child: Image.asset('assets/img/logo-nav.png', width: 96),
                ),
                const SizedBox(height: 20),
                Text('KINTSUGI', style: Theme.of(context).textTheme.headlineLarge),
                const SizedBox(height: 12),
                HintText(
                  session?.canSignIn == true
                      ? 'Sign in with $provider to continue.'
                      : 'Sign-in is required but $provider is not fully configured yet, so there is '
                          'nothing to sign in with. An administrator has to finish setting it up on '
                          'the Authentication settings screen.',
                  textAlign: TextAlign.center,
                ),
                const SizedBox(height: 24),
                SizedBox(
                  width: double.infinity,
                  child: PrimaryButton(
                    label: 'Continue with $provider',
                    onPressed: session?.canSignIn == true
                        ? () => context.read<SessionBloc>().add(const SignInRequested())
                        : null,
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
