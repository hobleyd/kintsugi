import 'package:get_it/get_it.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../../data/repositories/agent_package_repository_impl.dart';
import '../../data/repositories/application_repository_impl.dart';
import '../../data/repositories/host_repository_impl.dart';
import '../../data/repositories/remote_control_repository_impl.dart';
import '../../data/repositories/session_repository_impl.dart';
import '../../data/repositories/settings_repository_impl.dart';
import '../../data/repositories/upgrade_path_repository_impl.dart';
import '../../data/repositories/upgrade_script_repository_impl.dart';
import '../../domain/repositories/repositories.dart';
import '../../domain/usecases/application_usecases.dart';
import '../../domain/usecases/client_usecases.dart';
import '../../domain/usecases/host_usecases.dart';
import '../../domain/usecases/remote_control_usecases.dart';
import '../../domain/usecases/session_usecases.dart';
import '../../domain/usecases/settings_usecases.dart';
import '../../domain/usecases/upgrade_path_usecases.dart';
import '../../domain/usecases/upgrade_script_usecases.dart';
import '../network/api_client.dart';
import '../network/unauthorized_notifier.dart';
import '../platform/browser_page_navigator.dart';
import '../platform/page_navigator.dart';

final locator = GetIt.instance;

/// The composition root: the only place a concrete implementation is named.
///
/// Everything above this file depends on the abstractions in `domain/repositories/` — which is
/// what makes the presentation layer testable without a server, and what would let the transport
/// change without any screen knowing. Registered eagerly rather than lazily so a wiring mistake
/// surfaces at startup instead of on the screen that first needs it.
Future<void> configureDependencies() async {
  final preferences = await SharedPreferences.getInstance();
  locator.registerSingleton<SharedPreferences>(preferences);

  // Registered before the client that raises on it and the bloc that listens: a 401 from anywhere
  // in the app has to reach the session bloc, or an expired cookie shows up as an error string on
  // whichever screen happened to be open. See UnauthorizedNotifier for why that is a regression
  // worth this much wiring.
  locator.registerSingleton<UnauthorizedNotifier>(UnauthorizedNotifier());
  locator.registerSingleton<ApiClient>(
    ApiClient(unauthorizedNotifier: locator<UnauthorizedNotifier>()),
  );
  locator.registerSingleton<PageNavigator>(const BrowserPageNavigator());

  final api = locator<ApiClient>();

  locator
    ..registerSingleton<SessionRepository>(SessionRepositoryImpl(api, locator<PageNavigator>()))
    ..registerSingleton<HostRepository>(HostRepositoryImpl(api))
    ..registerSingleton<RemoteControlRepository>(RemoteControlRepositoryImpl(api))
    ..registerSingleton<ApplicationRepository>(ApplicationRepositoryImpl(api))
    ..registerSingleton<UpgradePathRepository>(UpgradePathRepositoryImpl(api))
    ..registerSingleton<AgentPackageRepository>(AgentPackageRepositoryImpl(api))
    ..registerSingleton<UpgradeScriptRepository>(UpgradeScriptRepositoryImpl(api))
    ..registerSingleton<AiAgentSettingsRepository>(AiAgentSettingsRepositoryImpl(api))
    ..registerSingleton<AuthenticationSettingsRepository>(AuthenticationSettingsRepositoryImpl(api))
    ..registerSingleton<GitHubSettingsRepository>(GitHubSettingsRepositoryImpl(api))
    ..registerSingleton<PatchingPolicySettingsRepository>(PatchingPolicySettingsRepositoryImpl(api))
    ..registerSingleton<VantaSettingsRepository>(VantaSettingsRepositoryImpl(api));

  _registerUseCases();
}

void _registerUseCases() {
  final session = locator<SessionRepository>();
  final hosts = locator<HostRepository>();
  final applications = locator<ApplicationRepository>();
  final upgradePaths = locator<UpgradePathRepository>();
  final packages = locator<AgentPackageRepository>();
  final scripts = locator<UpgradeScriptRepository>();
  final ai = locator<AiAgentSettingsRepository>();
  final auth = locator<AuthenticationSettingsRepository>();
  final gitHub = locator<GitHubSettingsRepository>();
  final policy = locator<PatchingPolicySettingsRepository>();
  final vanta = locator<VantaSettingsRepository>();
  final remoteControl = locator<RemoteControlRepository>();

  locator
    ..registerSingleton(ReadSession(session))
    ..registerSingleton(SignIn(session))
    ..registerSingleton(SignOut(session))
    ..registerSingleton(GetHosts(hosts))
    ..registerSingleton(RequestHostRemoval(hosts))
    ..registerSingleton(RequestRemoteControlSession(remoteControl))
    ..registerSingleton(GetRemoteControlSession(remoteControl))
    ..registerSingleton(EndRemoteControlSession(remoteControl))
    ..registerSingleton(OpenRemoteControlStream(remoteControl))
    ..registerSingleton(GetApplicationOverview(applications))
    ..registerSingleton(StartUpgradePathScan(upgradePaths))
    ..registerSingleton(GetUpgradePathScanStatus(upgradePaths))
    ..registerSingleton(StartUpdateCheck(upgradePaths))
    ..registerSingleton(GetUpdateCheckStatus(upgradePaths))
    ..registerSingleton(GetUpgradePathPrompt(upgradePaths))
    ..registerSingleton(StartUpgradePathRefresh(upgradePaths))
    ..registerSingleton(GetUpgradePathRefreshStatus(upgradePaths))
    ..registerSingleton(SaveUpgradePath(upgradePaths))
    ..registerSingleton(SignUpgradePathScript(upgradePaths))
    ..registerSingleton(GetClientsView(packages))
    ..registerSingleton(RefreshClients(packages))
    ..registerSingleton(GetUpgradeScriptsView(scripts))
    ..registerSingleton(RefreshApprovedScripts(scripts))
    ..registerSingleton(AdoptApprovedScript(scripts))
    ..registerSingleton(TakeServerWrittenScript(scripts))
    ..registerSingleton(GetAiAgentSettings(ai))
    ..registerSingleton(UpdateAiAgentSettings(ai))
    ..registerSingleton(GetOllamaModels(ai))
    ..registerSingleton(CheckGooseCliStatus(ai))
    ..registerSingleton(CheckClaudeAgentSdkStatus(ai))
    ..registerSingleton(GetAuthenticationSettings(auth))
    ..registerSingleton(UpdateAuthenticationSettings(auth))
    ..registerSingleton(GetGitHubSettings(gitHub))
    ..registerSingleton(UpdateGitHubSettings(gitHub))
    ..registerSingleton(GetPatchingPolicySettings(policy))
    ..registerSingleton(UpdatePatchingPolicySettings(policy))
    ..registerSingleton(GetVantaSettings(vanta))
    ..registerSingleton(UpdateVantaSettings(vanta))
    ..registerSingleton(GetVantaSyncStatus(vanta))
    ..registerSingleton(StartVantaSync(vanta));
}
