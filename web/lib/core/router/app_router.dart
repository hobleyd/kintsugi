import 'package:go_router/go_router.dart';

import '../../presentation/applications/applications_screen.dart';
import '../../presentation/clients/clients_screen.dart';
import '../../presentation/hosts/hosts_screen.dart';
import '../../presentation/session/session_bloc.dart';
import '../../presentation/session/sign_in_screen.dart';
import '../../presentation/session/startup_screens.dart';
import '../../presentation/settings/ai_agent_screen.dart';
import '../../presentation/settings/authentication_screen.dart';
import '../../presentation/settings/github_screen.dart';
import '../../presentation/settings/patching_policy_screen.dart';
import '../../presentation/settings/vanta_screen.dart';
import '../../presentation/shell/app_shell.dart';
import '../../presentation/upgrade_scripts/upgrade_scripts_screen.dart';
import 'bloc_listenable.dart';

/// Every path the UI has. Named constants because the sidebar, the redirects and the deep links
/// from one screen into another all have to agree on them.
abstract final class Routes {
  static const hosts = '/hosts';
  static const applications = '/applications';
  static const clients = '/clients';
  static const upgradeScripts = '/upgrade-scripts';
  static const settingsAiAgent = '/settings/ai-agent';
  static const settingsAuthentication = '/settings/authentication';
  static const settingsGitHub = '/settings/github';
  static const settingsPatchingPolicy = '/settings/patching-policy';
  static const settingsVanta = '/settings/vanta';
  static const signIn = '/login';
  static const starting = '/starting';
  static const unavailable = '/unavailable';
}

/// Builds the router, with the session gate as a redirect.
///
/// These paths are the same ones the Razor pages answered on, deliberately: a bookmark, a link in
/// a runbook, or the Hosts screen's own deep link into `/applications?status=update-available&host=…`
/// all still work. nginx serves `index.html` for any path it does not recognise, which is what
/// makes a real URL possible here rather than a fragment.
GoRouter createRouter(SessionBloc sessionBloc) {
  return GoRouter(
    initialLocation: Routes.hosts,
    refreshListenable: BlocListenable(sessionBloc.stream),
    redirect: (context, state) {
      final session = sessionBloc.state;
      final path = state.uri.path;

      // The bootstrap call has not answered yet. Nothing can be decided, so hold on the splash
      // rather than guessing — guessing flashes the sign-in screen at a server that has no
      // provider configured, or the app at a visitor who is not allowed in.
      if (session is SessionLoading) {
        return path == Routes.starting ? null : Routes.starting;
      }

      if (session is SessionUnavailable) {
        return path == Routes.unavailable ? null : Routes.unavailable;
      }

      final ready = session as SessionReady;

      // Nothing saved on the Authentication screen yet. This is the fresh-deploy lockdown that
      // used to be a 302 in Program.cs: there is no way to sign in and no administrator has
      // decided whether sign-in is required, so everything else is closed until one has.
      if (!ready.session.authenticationSettingsSaved) {
        return path == Routes.settingsAuthentication ? null : Routes.settingsAuthentication;
      }

      if (ready.session.authenticationEnabled && !ready.session.signedIn) {
        return path == Routes.signIn ? null : Routes.signIn;
      }

      // Signed in, or sign-in is off. The three gate routes are no longer where this browser
      // should be sitting.
      if (path == Routes.signIn || path == Routes.starting || path == Routes.unavailable) {
        return Routes.hosts;
      }

      return null;
    },
    routes: [
      GoRoute(path: '/', redirect: (_, _) => Routes.hosts),
      GoRoute(path: Routes.starting, builder: (_, _) => const StartingScreen()),
      GoRoute(path: Routes.unavailable, builder: (_, _) => const ServerUnavailableScreen()),
      GoRoute(path: Routes.signIn, builder: (_, _) => const SignInScreen()),
      ShellRoute(
        builder: (context, state, child) => AppShell(location: state.uri.path, child: child),
        routes: [
          GoRoute(path: Routes.hosts, builder: (_, _) => const HostsScreen()),
          GoRoute(
            path: Routes.applications,
            builder: (context, state) => ApplicationsScreen(
              // Deep-link filters, as the old page read them off window.location.search: the
              // Hosts screen's "N app updates" badge links straight to a filtered view.
              initialStatusKey: state.uri.queryParameters['status'],
              initialHostName: state.uri.queryParameters['host'],
            ),
          ),
          GoRoute(path: Routes.clients, builder: (_, _) => const ClientsScreen()),
          GoRoute(path: Routes.upgradeScripts, builder: (_, _) => const UpgradeScriptsScreen()),
          GoRoute(path: Routes.settingsAiAgent, builder: (_, _) => const AiAgentSettingsScreen()),
          GoRoute(
            path: Routes.settingsAuthentication,
            builder: (_, _) => const AuthenticationSettingsScreen(),
          ),
          GoRoute(path: Routes.settingsGitHub, builder: (_, _) => const GitHubSettingsScreen()),
          GoRoute(
            path: Routes.settingsPatchingPolicy,
            builder: (_, _) => const PatchingPolicySettingsScreen(),
          ),
          GoRoute(path: Routes.settingsVanta, builder: (_, _) => const VantaSettingsScreen()),
        ],
      ),
    ],
  );
}
