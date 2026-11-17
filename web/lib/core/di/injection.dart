import 'package:shared_preferences/shared_preferences.dart';

import '../../data/repositories/agent_package_repository_impl.dart';
import '../../data/repositories/ai_usage_repository_impl.dart';
import '../../data/repositories/application_repository_impl.dart';
import '../../data/repositories/managed_application_repository_impl.dart';
import '../../data/repositories/host_repository_impl.dart';
import '../../data/repositories/autonomy_repository_impl.dart';
import '../../data/repositories/device_report_repository_impl.dart';
import '../../data/repositories/baseline_repository_impl.dart';
import '../../data/repositories/host_activity_repository_impl.dart';
import '../../data/repositories/patch_failure_repository_impl.dart';
import '../../data/repositories/remote_control_repository_impl.dart';
import '../../data/repositories/server_info_repository_impl.dart';
import '../../data/repositories/session_repository_impl.dart';
import '../../data/repositories/settings_repository_impl.dart';
import '../../data/repositories/upgrade_path_repository_impl.dart';
import '../../data/repositories/upgrade_script_repository_impl.dart';
import '../../domain/repositories/repositories.dart';
import '../../data/repositories/vulnerability_repository_impl.dart';
import '../../domain/usecases/ai_usage_usecases.dart';
import '../../domain/usecases/application_usecases.dart';
import '../../domain/usecases/managed_application_usecases.dart';
import '../../domain/usecases/client_usecases.dart';
import '../../domain/usecases/host_usecases.dart';
import '../../domain/usecases/autonomy_usecases.dart';
import '../../domain/usecases/device_report_usecases.dart';
import '../../domain/usecases/baseline_usecases.dart';
import '../../domain/usecases/host_activity_usecases.dart';
import '../../domain/usecases/patch_failure_usecases.dart';
import '../../domain/usecases/remote_control_usecases.dart';
import '../../domain/usecases/server_info_usecases.dart';
import '../../domain/usecases/session_usecases.dart';
import '../../domain/usecases/settings_usecases.dart';
import '../../domain/usecases/upgrade_path_usecases.dart';
import '../../domain/usecases/upgrade_script_usecases.dart';
import '../../domain/usecases/vulnerability_usecases.dart';
import '../diagnostics/diagnostics_log.dart';
import '../diagnostics/diagnostics_store.dart';
import '../network/api_client.dart';
import '../network/unauthorized_notifier.dart';
import '../platform/browser_file_saver.dart';
import '../platform/browser_full_screen.dart';
import '../platform/browser_file_downloader.dart';
import '../platform/browser_page_navigator.dart';
import '../platform/browser_storage_events.dart';
import '../platform/file_downloader.dart';
import '../platform/file_saver.dart';
import '../platform/full_screen.dart';
import '../platform/page_navigator.dart';
import 'locator.dart';

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
  // One per page load, deliberately: the panel it feeds is the shell's, and its whole purpose is
  // to outlive the screen that recorded into it. A per-screen instance would be the alert box it
  // replaces. Backed by `localStorage` so it also outlives the page load, and so a second tab —
  // the Diagnostics page opened on its own — shows the same runs. The watcher is given the key as
  // `shared_preferences` writes it on the web, which has a `flutter.` prefix. See DiagnosticsLog.
  locator.registerSingleton<DiagnosticsLog>(
    DiagnosticsLog(
      store: SharedPreferencesDiagnosticsStore(
        preferences,
        changedElsewhere:
            browserStorageChanges('flutter.${SharedPreferencesDiagnosticsStore.storageKey}'),
      ),
    ),
  );
  locator.registerSingleton<FileDownloader>(const BrowserFileDownloader());
  // A singleton because it holds one broadcast stream over one document-level listener: a
  // per-screen instance would add a listener per remote-control session and never remove it.
  locator.registerSingleton<FullScreenController>(BrowserFullScreenController());
  locator.registerSingleton<FileSaver>(const BrowserFileSaver());

  final api = locator<ApiClient>();

  locator
    ..registerSingleton<SessionRepository>(SessionRepositoryImpl(api, locator<PageNavigator>()))
    ..registerSingleton<ServerInfoRepository>(ServerInfoRepositoryImpl(api))
    ..registerSingleton<HostRepository>(HostRepositoryImpl(api))
    ..registerSingleton<RemoteControlRepository>(RemoteControlRepositoryImpl(api))
    ..registerSingleton<ApplicationRepository>(ApplicationRepositoryImpl(api))
    ..registerSingleton<PatchFailureRepository>(PatchFailureRepositoryImpl(api))
    ..registerSingleton<HostActivityRepository>(HostActivityRepositoryImpl(api))
    ..registerSingleton<AutonomyRepository>(AutonomyRepositoryImpl(api))
    ..registerSingleton<DeviceReportRepository>(DeviceReportRepositoryImpl(api))
    ..registerSingleton<BaselineRepository>(BaselineRepositoryImpl(api))
    ..registerSingleton<UpgradePathRepository>(UpgradePathRepositoryImpl(api))
    ..registerSingleton<AgentPackageRepository>(AgentPackageRepositoryImpl(api))
    ..registerSingleton<UpgradeScriptRepository>(UpgradeScriptRepositoryImpl(api))
    ..registerSingleton<AiRoutingRepository>(AiRoutingRepositoryImpl(api))
    ..registerSingleton<AiAgentSettingsRepository>(AiAgentSettingsRepositoryImpl(api))
    ..registerSingleton<AiUsageRepository>(AiUsageRepositoryImpl(api))
    ..registerSingleton<ManagedApplicationRepository>(ManagedApplicationRepositoryImpl(api))
    ..registerSingleton<AdministratorsRepository>(AdministratorsRepositoryImpl(api))
    ..registerSingleton<AuditSettingsRepository>(AuditSettingsRepositoryImpl(api))
    ..registerSingleton<AuthenticationSettingsRepository>(AuthenticationSettingsRepositoryImpl(api))
    ..registerSingleton<GitHubSettingsRepository>(GitHubSettingsRepositoryImpl(api, locator<PageNavigator>()))
    ..registerSingleton<PatchingPolicySettingsRepository>(PatchingPolicySettingsRepositoryImpl(api))
    ..registerSingleton<VantaSettingsRepository>(VantaSettingsRepositoryImpl(api))
    ..registerSingleton<VulnerabilityRepository>(VulnerabilityRepositoryImpl(api))
    ..registerSingleton<VulnerabilitySettingsRepository>(VulnerabilitySettingsRepositoryImpl(api));

  _registerUseCases();
}

void _registerUseCases() {
  final session = locator<SessionRepository>();
  final server = locator<ServerInfoRepository>();
  final hosts = locator<HostRepository>();
  final applications = locator<ApplicationRepository>();
  final upgradePaths = locator<UpgradePathRepository>();
  final patchFailures = locator<PatchFailureRepository>();
  final hostActivity = locator<HostActivityRepository>();
  final autonomy = locator<AutonomyRepository>();
  final deviceReport = locator<DeviceReportRepository>();
  final baselines = locator<BaselineRepository>();
  final packages = locator<AgentPackageRepository>();
  final scripts = locator<UpgradeScriptRepository>();
  final ai = locator<AiAgentSettingsRepository>();
  final aiUsage = locator<AiUsageRepository>();
  final audit = locator<AuditSettingsRepository>();
  final auth = locator<AuthenticationSettingsRepository>();
  final managed = locator<ManagedApplicationRepository>();
  final administrators = locator<AdministratorsRepository>();
  final gitHub = locator<GitHubSettingsRepository>();
  final policy = locator<PatchingPolicySettingsRepository>();
  final vanta = locator<VantaSettingsRepository>();
  final vulnerabilities = locator<VulnerabilityRepository>();
  final vulnerabilitySettings = locator<VulnerabilitySettingsRepository>();
  final remoteControl = locator<RemoteControlRepository>();

  locator
    ..registerSingleton(ReadSession(session))
    ..registerSingleton(SignIn(session))
    ..registerSingleton(SignOut(session))
    ..registerSingleton(GetServerVersion(server))
    ..registerSingleton(GetHosts(hosts))
    ..registerSingleton(RequestHostRemoval(hosts))
    ..registerSingleton(RequestRemoteControlSession(remoteControl))
    ..registerSingleton(GetRemoteControlSession(remoteControl))
    ..registerSingleton(EndRemoteControlSession(remoteControl))
    ..registerSingleton(OpenRemoteControlStream(remoteControl))
    ..registerSingleton(GetManagedApplications(managed))
    ..registerSingleton(MarkApplicationsManaged(managed))
    ..registerSingleton(UnmarkApplicationManaged(managed))
    ..registerSingleton(AddManagedOwner(managed))
    ..registerSingleton(RemoveManagedOwner(managed))
    ..registerSingleton(GetApplicationOverview(applications))
    ..registerSingleton(RequestForcedPatchRuns(applications))
    ..registerSingleton(GetAutonomy(autonomy))
    ..registerSingleton(GetAutonomyCauseDevices(autonomy))
    ..registerSingleton(PatchAutonomyCause(autonomy))
    ..registerSingleton(GetDeviceReport(deviceReport))
    ..registerSingleton(GetLatestLogCollection(deviceReport))
    ..registerSingleton(RequestLogCollection(deviceReport))
    ..registerSingleton(DeleteLogCollection(deviceReport))
    ..registerSingleton(GetLogEntries(deviceReport))
    ..registerSingleton(DownloadLogCollection(deviceReport))
    ..registerSingleton(SearchDevices(deviceReport))
    ..registerSingleton(GetBaselines(baselines))
    ..registerSingleton(GetBaseline(baselines))
    ..registerSingleton(GetBaselineHost(baselines))
    ..registerSingleton(ImportBaseline(baselines))
    ..registerSingleton(SignBaselineVersion(baselines))
    ..registerSingleton(AssignBaseline(baselines))
    ..registerSingleton(GetHostActivity(hostActivity))
    ..registerSingleton(SetHostActivityRetention(hostActivity))
    ..registerSingleton(GetPatchFailures(patchFailures))
    ..registerSingleton(DismissPatchFailure(patchFailures))
    ..registerSingleton(StartUpgradePathScan(upgradePaths))
    ..registerSingleton(GetUpgradePathScanStatus(upgradePaths))
    ..registerSingleton(GetUpgradePathScanResults(upgradePaths))
    ..registerSingleton(GetUpdateCheckResults(upgradePaths))
    ..registerSingleton(StartUpdateCheck(upgradePaths))
    ..registerSingleton(GetUpdateCheckStatus(upgradePaths))
    ..registerSingleton(CheckApplicationUpdate(upgradePaths))
    ..registerSingleton(GetUpgradePathPrompt(upgradePaths))
    ..registerSingleton(StartUpgradePathRefresh(upgradePaths))
    ..registerSingleton(GetUpgradePathRefreshStatus(upgradePaths))
    ..registerSingleton(SaveUpgradePath(upgradePaths))
    ..registerSingleton(SignUpgradePathScript(upgradePaths))
    ..registerSingleton(GetClientsView(packages))
    ..registerSingleton(RefreshClients(packages))
    ..registerSingleton(GetWindowsBootstrapScript(packages))
    ..registerSingleton(GetUpgradeScriptsView(scripts))
    ..registerSingleton(RefreshApprovedScripts(scripts))
    ..registerSingleton(AdoptApprovedScript(scripts))
    ..registerSingleton(TakeServerWrittenScript(scripts))
    ..registerSingleton(GetAiAgentSettings(ai))
    ..registerSingleton(GetAiUsage(aiUsage))
    ..registerSingleton(SaveAiBudget(aiUsage))
    ..registerSingleton(ManageAiRouting(locator<AiRoutingRepository>()))
    ..registerSingleton(UpdateAiAgentSettings(ai))
    ..registerSingleton(GetOllamaModels(ai))
    ..registerSingleton(CheckGooseCliStatus(ai))
    ..registerSingleton(CheckClaudeAgentSdkStatus(ai))
    ..registerSingleton(GetAuditSettings(audit))
    ..registerSingleton(UpdateAuditSettings(audit))
    ..registerSingleton(GetAdministrators(administrators))
    ..registerSingleton(AddAdministrators(administrators))
    ..registerSingleton(RemoveAdministrator(administrators))
    ..registerSingleton(GetAuthenticationSettings(auth))
    ..registerSingleton(UpdateAuthenticationSettings(auth))
    ..registerSingleton(GetGitHubSettings(gitHub))
    ..registerSingleton(UpdateGitHubSettings(gitHub))
    ..registerSingleton(StartGitHubAppSetup(gitHub))
    ..registerSingleton(DisconnectGitHubApp(gitHub))
    ..registerSingleton(GetPatchingPolicySettings(policy))
    ..registerSingleton(UpdatePatchingPolicySettings(policy))
    ..registerSingleton(GetVantaSettings(vanta))
    ..registerSingleton(UpdateVantaSettings(vanta))
    ..registerSingleton(GetVantaSyncStatus(vanta))
    ..registerSingleton(StartVantaSync(vanta))
    ..registerSingleton(GetVulnerabilityOverview(vulnerabilities))
    ..registerSingleton(GetCpeMappings(vulnerabilities))
    ..registerSingleton(SearchCpeDictionary(vulnerabilities))
    ..registerSingleton(ConfirmCpeMapping(vulnerabilities))
    ..registerSingleton(ConfirmCpeMappings(vulnerabilities))
    ..registerSingleton(ResetCpeMappings(vulnerabilities))
    ..registerSingleton(MarkCpeMappingNotApplicable(vulnerabilities))
    ..registerSingleton(ResetCpeMapping(vulnerabilities))
    ..registerSingleton(GetVulnerabilityRunStatus(vulnerabilities))
    ..registerSingleton(CancelVulnerabilityRun(vulnerabilities))
    ..registerSingleton(StartVulnerabilityRun(vulnerabilities))
    ..registerSingleton(GetVulnerabilitySettings(vulnerabilitySettings))
    ..registerSingleton(UpdateVulnerabilitySettings(vulnerabilitySettings));
}
