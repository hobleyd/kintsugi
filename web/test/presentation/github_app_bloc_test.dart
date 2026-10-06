import 'package:bloc_test/bloc_test.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/domain/entities/settings.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/settings_usecases.dart';
import 'package:kintsugi_web/presentation/settings/github_bloc.dart';
import 'package:kintsugi_web/presentation/settings/settings_state.dart';

const _installed = GitHubSettings(
  agentPackageRepository: 'acme/kintsugi',
  isAgentPackageRepositoryDefault: false,
  scriptApprovalRepository: 'acme/approvals',
  isScriptApprovalRepositoryDefault: false,
  hasApiToken: true,
  hasScriptApprovalToken: true,
  gitHubAppSlug: 'kintsugi-acme',
  gitHubAppOwner: 'acme',
  isGitHubAppInstalled: true,
);

class _FakeGitHubSettingsRepository implements GitHubSettingsRepository {
  final setupCalls = <(String?, String?)>[];
  ApiException? setupFailure;

  @override
  Future<GitHubSettings> read() async => _installed;

  @override
  Future<GitHubSettings> update({
    required String? agentPackageRepository,
    required String? scriptApprovalRepository,
    required String? apiToken,
    required bool clearApiToken,
    required String? scriptApprovalToken,
    required bool clearScriptApprovalToken,
  }) async =>
      _installed;

  @override
  Future<void> startGitHubAppSetup({required String? organization, required String? name}) async {
    setupCalls.add((organization, name));
    if (setupFailure != null) {
      throw setupFailure!;
    }
  }

  @override
  Future<GitHubSettings> disconnectGitHubApp() async => const GitHubSettings(
        agentPackageRepository: 'acme/kintsugi',
        isAgentPackageRepositoryDefault: false,
        scriptApprovalRepository: 'acme/approvals',
        isScriptApprovalRepositoryDefault: false,
        hasApiToken: true,
        hasScriptApprovalToken: true,
      );
}

GitHubSettingsBloc _bloc(_FakeGitHubSettingsRepository repository) => GitHubSettingsBloc(
      getSettings: GetGitHubSettings(repository),
      updateSettings: UpdateGitHubSettings(repository),
      startAppSetup: StartGitHubAppSetup(repository),
      disconnectApp: DisconnectGitHubApp(repository),
    );

void main() {
  final repository = _FakeGitHubSettingsRepository();

  blocTest<GitHubSettingsBloc, SettingsState<GitHubSettings>>(
    'starting setup stays busy, because the page is about to navigate to GitHub',
    build: () => _bloc(repository..setupCalls.clear()..setupFailure = null),
    act: (bloc) => bloc.add(const GitHubAppSetupRequested(organization: 'acme', name: '')),
    expect: () => [
      isA<SettingsState<GitHubSettings>>().having((s) => s.saving, 'saving', isTrue),
    ],
    verify: (_) => expect(repository.setupCalls, [('acme', '')]),
  );

  blocTest<GitHubSettingsBloc, SettingsState<GitHubSettings>>(
    'a refused setup reports why and re-enables the button',
    build: () => _bloc(repository..setupFailure = const ApiException('That is not a GitHub organisation name.')),
    act: (bloc) => bloc.add(const GitHubAppSetupRequested(organization: 'not valid', name: null)),
    expect: () => [
      isA<SettingsState<GitHubSettings>>().having((s) => s.saving, 'saving', isTrue),
      isA<SettingsState<GitHubSettings>>()
          .having((s) => s.saving, 'saving', isFalse)
          .having((s) => s.error, 'error', 'That is not a GitHub organisation name.'),
    ],
  );

  blocTest<GitHubSettingsBloc, SettingsState<GitHubSettings>>(
    'disconnecting replaces the settings with the server\'s answer',
    build: () => _bloc(repository..setupFailure = null),
    act: (bloc) => bloc.add(const GitHubAppDisconnectRequested()),
    expect: () => [
      isA<SettingsState<GitHubSettings>>().having((s) => s.saving, 'saving', isTrue),
      isA<SettingsState<GitHubSettings>>()
          .having((s) => s.saving, 'saving', isFalse)
          .having((s) => s.value?.hasGitHubApp, 'hasGitHubApp', isFalse),
    ],
  );
}
