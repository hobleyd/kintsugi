import 'package:go_router/go_router.dart';

import '../../domain/entities/enums.dart';
import '../../presentation/applications/applications_screen.dart';
import '../../presentation/applications/failed_updates_screen.dart';
import '../../presentation/clients/clients_screen.dart';
import '../../presentation/hosts/hosts_screen.dart';
import '../../presentation/remote_control/remote_control_screen.dart';
import '../../presentation/session/session_bloc.dart';
import '../../presentation/session/sign_in_screen.dart';
import '../../presentation/session/startup_screens.dart';
import '../../presentation/settings/ai_agent_screen.dart';
import '../../presentation/settings/auditing_screen.dart';
import '../../presentation/settings/authentication_screen.dart';
import '../../presentation/settings/github_screen.dart';
import '../../presentation/settings/patching_policy_screen.dart';
import '../../presentation/settings/vanta_screen.dart';
import '../../presentation/shell/app_shell.dart';
import '../../presentation/settings/vulnerabilities_screen.dart';
import '../../presentation/upgrade_scripts/upgrade_scripts_screen.dart';
import '../../presentation/vulnerabilities/cve_mapping_screen.dart';
import '../../presentation/vulnerabilities/cve_reporting_screen.dart';
import '../di/locator.dart';
import '../platform/full_screen.dart';
import 'bloc_listenable.dart';

/// Every path the UI has. Named constants because the sidebar, the redirects and the deep links
/// from one screen into another all have to agree on them.
abstract final class Routes {
  static const hosts = '/hosts';

  /// One host's remote control session. A real path rather than a dialog, so a support call has an
  /// address somebody can be sent, and so leaving it is an ordinary navigation that the screen can
  /// hang the session up on.
  static String remoteControl(String hostId) => '/hosts/${Uri.encodeComponent(hostId)}/remote';

  /// One host's terminal. Its own address rather than a mode of the one above, for the same reason:
  /// a support call is pointed at a link, and "open a shell on this machine" and "watch this
  /// machine's screen" are different things to be sent to.
  static String remoteShell(String hostId) => '/hosts/${Uri.encodeComponent(hostId)}/shell';
  /// The Applications menu's two screens. `/applications` stays the installed-applications view
  /// rather than moving under a new prefix, because the Hosts screen's "N app updates" badge and
  /// every Vanta record's `externalUrl` deep-link into it with `?status=&host=` — see
  /// `VantaResourceBuilder`.
  static const applications = '/applications';
  static const applicationsFailed = '/applications/failed';
  static const clients = '/clients';

  /// The Vulnerabilities menu's two screens. Top-level rather than under Applications: the
  /// question they answer is about the fleet, not about one application's update state.
  ///
  /// `/vulnerabilities` stays CVE Reporting rather than moving under a new prefix, the same way
  /// `/applications` stayed Currently Installed — it is the address anything already pointing here
  /// points at, and it is what somebody opening the menu came to read.
  static const vulnerabilities = '/vulnerabilities';
  static const vulnerabilitiesMapping = '/vulnerabilities/mapping';

  static const upgradeScripts = '/upgrade-scripts';
  static const settingsAiAgent = '/settings/ai-agent';
  static const settingsAuditing = '/settings/auditing';
  static const settingsAuthentication = '/settings/authentication';
  static const settingsGitHub = '/settings/github';
  static const settingsPatchingPolicy = '/settings/patching-policy';
  static const settingsVanta = '/settings/vanta';
  static const settingsVulnerabilities = '/settings/vulnerabilities';
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
        builder: (context, state, child) => AppShell(
          location: state.uri.path,
          // So the sidebar steps aside for the remote-control viewer's full-screen mode. Passed
          // here rather than looked up inside the shell so a widget test can pump it without one.
          fullScreen: locator<FullScreenController>(),
          child: child,
        ),
        routes: [
          GoRoute(path: Routes.hosts, builder: (_, _) => const HostsScreen()),
          GoRoute(
            // Nested under the host it controls, so the address says what it is. The hostname rides
            // along as a query parameter purely so the heading can name the host before the first
            // response arrives; the screen works without it.
            path: '/hosts/:hostId/remote',
            builder: (context, state) => RemoteControlScreen(
              hostId: state.pathParameters['hostId']!,
              hostname: state.uri.queryParameters['hostname'],
              // The viewer asks for full screen as it opens: a remote desktop inside a 240px-inset
              // panel is the host's screen at half the size it could be.
              fullScreen: locator<FullScreenController>(),
            ),
          ),
          GoRoute(
            // Beside /remote and deliberately not a query parameter on it: the two open different
            // things, and one of them asks the host's user first.
            path: '/hosts/:hostId/shell',
            builder: (context, state) => RemoteControlScreen(
              hostId: state.pathParameters['hostId']!,
              kind: RemoteControlSessionKind.shell,
              hostname: state.uri.queryParameters['hostname'],
              // A terminal wants the height as much as a desktop wants the width, and both are the
              // same screen — so the toggle is offered on this route too.
              fullScreen: locator<FullScreenController>(),
            ),
          ),
          GoRoute(
            path: Routes.applications,
            builder: (context, state) => ApplicationsScreen(
              // Deep-link filters, as the old page read them off window.location.search: the
              // Hosts screen's "N app updates" badge links straight to a filtered view.
              initialStatusKey: state.uri.queryParameters['status'],
              initialHostName: state.uri.queryParameters['host'],
            ),
          ),
          GoRoute(
            path: Routes.applicationsFailed,
            builder: (_, _) => const FailedUpdatesScreen(),
          ),
          GoRoute(path: Routes.clients, builder: (_, _) => const ClientsScreen()),
          GoRoute(path: Routes.upgradeScripts, builder: (_, _) => const UpgradeScriptsScreen()),
          GoRoute(path: Routes.settingsAiAgent, builder: (_, _) => const AiAgentSettingsScreen()),
          GoRoute(path: Routes.settingsAuditing, builder: (_, _) => const AuditingSettingsScreen()),
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
          GoRoute(
            path: Routes.settingsVulnerabilities,
            builder: (_, _) => const VulnerabilitiesSettingsScreen(),
          ),
          GoRoute(path: Routes.vulnerabilities, builder: (_, _) => const CveReportingScreen()),
          GoRoute(
            path: Routes.vulnerabilitiesMapping,
            builder: (_, _) => const CveMappingScreen(),
          ),
        ],
      ),
    ],
  );
}
