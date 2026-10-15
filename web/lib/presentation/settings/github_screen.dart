import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/locator.dart';
import '../../core/platform/page_navigator.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/settings.dart';
import '../../domain/usecases/settings_usecases.dart';
import 'github_bloc.dart';
import 'settings_state.dart';

/// Which GitHub repositories this server reads agent builds and script approvals from, and the
/// credentials for each. What `Pages/Settings/GitHub.cshtml` was.
class GitHubSettingsScreen extends StatelessWidget {
  const GitHubSettingsScreen({super.key, this.appError, this.appInstalled = false});

  /// `?githubAppError=` — what the server's GitHub App callback could not do. The callback is a
  /// browser navigation, so it reports by redirecting here rather than in a JSON body nobody sees.
  final String? appError;

  /// `?githubApp=installed` — the installation leg completed.
  final bool appInstalled;

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => GitHubSettingsBloc(
          getSettings: locator<GetGitHubSettings>(),
          updateSettings: locator<UpdateGitHubSettings>(),
          startAppSetup: locator<StartGitHubAppSetup>(),
          disconnectApp: locator<DisconnectGitHubApp>(),
        )..add(const GitHubSettingsRequested()),
        child: _GitHubForm(appError: appError, appInstalled: appInstalled),
      );
}

class _GitHubForm extends StatefulWidget {
  const _GitHubForm({required this.appError, required this.appInstalled});

  final String? appError;
  final bool appInstalled;

  @override
  State<_GitHubForm> createState() => _GitHubFormState();
}

class _GitHubFormState extends State<_GitHubForm> {
  final _agentPackageRepository = TextEditingController();
  final _scriptApprovalRepository = TextEditingController();
  final _apiToken = TextEditingController();
  final _scriptApprovalToken = TextEditingController();
  final _appOrganization = TextEditingController();
  final _appName = TextEditingController();

  bool _clearApiToken = false;
  bool _clearScriptApprovalToken = false;

  @override
  void dispose() {
    _agentPackageRepository.dispose();
    _scriptApprovalRepository.dispose();
    _apiToken.dispose();
    _scriptApprovalToken.dispose();
    _appOrganization.dispose();
    _appName.dispose();
    super.dispose();
  }

  void _hydrate(GitHubSettings settings) {
    // The effective values, defaults included, rather than blanks — the operator should see which
    // repositories this server is actually pointed at.
    _agentPackageRepository.text = settings.agentPackageRepository;
    _scriptApprovalRepository.text = settings.scriptApprovalRepository;

    // Proposed, not imposed: the App is most useful owned by the organisation the approval
    // repository lives in, since an installation token can only reach repositories on the
    // account the App is installed on.
    if (_appOrganization.text.isEmpty) {
      _appOrganization.text = _ownerOf(settings.scriptApprovalRepository) ?? '';
    }

    // Never repopulate a token field, even after a successful save: the value was never sent here
    // in the first place, and echoing a submitted one back would leave it sitting in the form.
    _apiToken.clear();
    _scriptApprovalToken.clear();
    setState(() {
      _clearApiToken = false;
      _clearScriptApprovalToken = false;
    });
  }

  void _edited() => context.read<GitHubSettingsBloc>().add(const GitHubSettingsEdited());

  void _createApp() => context.read<GitHubSettingsBloc>().add(GitHubAppSetupRequested(
        organization: _appOrganization.text.trim(),
        name: _appName.text.trim(),
      ));

  void _disconnectApp() =>
      context.read<GitHubSettingsBloc>().add(const GitHubAppDisconnectRequested());

  void _save() => context.read<GitHubSettingsBloc>().add(GitHubSettingsSaveRequested(
        agentPackageRepository: _agentPackageRepository.text.trim(),
        scriptApprovalRepository: _scriptApprovalRepository.text.trim(),
        apiToken: _apiToken.text.isEmpty ? null : _apiToken.text,
        clearApiToken: _clearApiToken,
        scriptApprovalToken: _scriptApprovalToken.text.isEmpty ? null : _scriptApprovalToken.text,
        clearScriptApprovalToken: _clearScriptApprovalToken,
      ));

  @override
  Widget build(BuildContext context) => BlocConsumer<GitHubSettingsBloc, SettingsState<GitHubSettings>>(
        listenWhen: (previous, current) => previous.value != current.value && current.value != null,
        listener: (context, state) => _hydrate(state.value!),
        builder: (context, state) {
          final settings = state.value;

          return PageScaffold(
            title: 'GitHub',
            subtitle: 'Which GitHub repositories this server reads agent builds and script approvals '
                'from, and the credentials it uses for each. These used to live in .env; if this '
                'deployment had them there, they were read once at startup to fill this screen in and '
                'those entries can now be deleted.',
            children: [
              if (widget.appError != null) AlertBox.error(widget.appError!),
              if (widget.appInstalled && settings?.isGitHubAppInstalled == true)
                const AlertBox.success(
                  'GitHub App installed. It now stands in for both tokens below.',
                ),
              if (state.error != null) AlertBox.error(state.error!),
              if (state.saved) const AlertBox.success('GitHub settings saved.'),
              _GitHubAppPanel(
                settings: settings,
                organization: _appOrganization,
                name: _appName,
                busy: state.saving || state.loading,
                onCreate: _createApp,
                onDisconnect: _disconnectApp,
              ),
              const SizedBox(height: 24),
              SettingsColumns(
                form: SettingsFormPanel(
                  maxWidth: double.infinity,
                  children: [
                    const SubHeadingTight('Agent builds'),
                    LabelledField(
                      label: 'Agent package repository',
                      hints: [
                        HintText(
                          'Where the Clients screen pulls kintsugi-agent builds from — its CI publishes '
                          'one release per agent per version. Leave blank for the default.'
                          '${settings?.isAgentPackageRepositoryDefault == true ? ' Currently using the default.' : ''}',
                        ),
                      ],
                      child: KintsugiTextField(
                        controller: _agentPackageRepository,
                        hintText: 'owner/name',
                        errorText: state.errorFor('AgentPackageRepository'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                    const SubHeadingTight('Script approvals'),
                    LabelledField(
                      label: 'Script approval repository',
                      hints: [
                        HintText(
                          'Where human-approved upgrade scripts are published and read back — see '
                          'Upgrade Scripts. Leave blank for the default.'
                          '${settings?.isScriptApprovalRepositoryDefault == true ? ' Currently using the default.' : ''}',
                        ),
                      ],
                      child: KintsugiTextField(
                        controller: _scriptApprovalRepository,
                        hintText: 'owner/name',
                        errorText: state.errorFor('ScriptApprovalRepository'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                    // Said here rather than only in CLAUDE.md, because it is the thing an operator
                    // is most likely to get wrong: this repository's default branch is what decides
                    // whose merges can offer executable content to this server.
                    const AlertBox.info(
                      "This repository's default branch is the trust root for script approval: "
                      'anyone who can merge there can offer a script that this server\'s agents may '
                      'end up running. Protect it with required reviewers, and point this at a '
                      'repository you control — approving anything needs write access to whatever it '
                      'names.',
                    ),
                  ],
                ),
                aside: KintsugiPanel(
                  padding: const EdgeInsets.all(28),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      const SubHeadingTight('Credentials'),
                      if (settings?.isGitHubAppInstalled == true) ...[
                        const AlertBox.info(
                          'The GitHub App above is installed, so neither token here is used — '
                          'the App mints a short-lived one in place of each. They are kept, not '
                          'cleared, so disconnecting the App falls back to them.',
                        ),
                        const SizedBox(height: 16),
                      ],
                      LabelledField(
                        label: 'Read-only API token',
                        hints: const [
                          HintText(
                            "Lifts GitHub's anonymous rate limit for upgrade-path research and for "
                            'listing agent builds. Optional — without it those reads are limited to 60 '
                            'requests an hour.',
                          ),
                        ],
                        child: KintsugiTextField(
                          controller: _apiToken,
                          obscureText: true,
                          hintText: settings?.hasApiToken == true
                              ? 'Stored — leave blank to keep it'
                              : 'Optional',
                          errorText: state.errorFor('ApiToken'),
                          onChanged: (_) => _edited(),
                        ),
                      ),
                      const SizedBox(height: 10),
                      const _TokenHelp(
                        steps: [
                          'On GitHub, go to Settings → Developer settings → Personal access tokens → '
                              'Fine-grained tokens and choose Generate new token.',
                          'Give it a name and an expiry. Note the expiry: reads fall back to the '
                              'anonymous rate limit when it lapses.',
                          'Add no permissions at all. A fine-grained token already carries read-only '
                              'access to every public repository, which is all this one is for.',
                          'Generate it, copy the value — GitHub shows it once — and paste it above.',
                        ],
                      ),
                      if (settings?.hasApiToken == true) ...[
                        const SizedBox(height: 8),
                        KintsugiCheckbox(
                          label: 'Remove the stored token',
                          value: _clearApiToken,
                          onChanged: (value) {
                            setState(() => _clearApiToken = value);
                            _edited();
                          },
                        ),
                      ],
                      const SizedBox(height: 22),
                      LabelledField(
                        label: 'Script approval token',
                        hints: const [
                          HintText(
                            'Opens the pull request that records a signed script. Kept separate from '
                            'the token above on purpose: that one is handed to the AI research client '
                            'and the agent-build reader as well, and neither has any business holding a '
                            'credential that can write. Without this, signing still approves a script '
                            "here and this server's agents still run it, but nothing is recorded "
                            'upstream and no other server can pick it up.',
                          ),
                        ],
                        child: KintsugiTextField(
                          controller: _scriptApprovalToken,
                          obscureText: true,
                          hintText: settings?.hasScriptApprovalToken == true
                              ? 'Stored — leave blank to keep it'
                              : 'Optional',
                          errorText: state.errorFor('ScriptApprovalToken'),
                          onChanged: (_) => _edited(),
                        ),
                      ),
                      const SizedBox(height: 10),
                      _TokenHelp(
                        steps: [
                          'Same place — Fine-grained tokens → Generate new token.',
                          'Under Repository access choose Only select repositories and pick '
                              '${settings?.scriptApprovalRepository ?? 'the approval repository'}.',
                          'Under Permissions → Repository permissions set Contents to Read and write '
                              'and Pull requests to Read and write. Nothing else.',
                          'Generate it, copy the value, and paste it above.',
                        ],
                      ),
                      if (settings?.hasScriptApprovalToken == true) ...[
                        const SizedBox(height: 8),
                        KintsugiCheckbox(
                          label: 'Remove the stored token',
                          value: _clearScriptApprovalToken,
                          onChanged: (value) {
                            setState(() => _clearScriptApprovalToken = value);
                            _edited();
                          },
                        ),
                      ],
                    ],
                  ),
                ),
              ),
              const SizedBox(height: 24),
              Align(
                alignment: Alignment.centerLeft,
                child: PrimaryButton(
                  label: 'Save',
                  busy: state.saving,
                  onPressed: state.loading ? null : _save,
                ),
              ),
            ],
          );
        },
      );
}

String? _ownerOf(String repository) {
  final slash = repository.indexOf('/');
  return slash <= 0 ? null : repository.substring(0, slash);
}

/// The GitHub App: create it, install it, or see what it is and disconnect it.
///
/// Above the two tokens rather than beside them because it replaces them — once installed, the
/// server mints a narrowed installation token in place of each, and Sign Script's pull requests
/// are opened by the App's bot rather than by whichever person's token was pasted in.
class _GitHubAppPanel extends StatelessWidget {
  const _GitHubAppPanel({
    required this.settings,
    required this.organization,
    required this.name,
    required this.busy,
    required this.onCreate,
    required this.onDisconnect,
  });

  final GitHubSettings? settings;
  final TextEditingController organization;
  final TextEditingController name;
  final bool busy;
  final VoidCallback onCreate;
  final VoidCallback onDisconnect;

  @override
  Widget build(BuildContext context) {
    final settings = this.settings;
    final installUrl = settings?.gitHubAppInstallUrl;
    final approvalOwner = settings == null ? null : _ownerOf(settings.scriptApprovalRepository);
    final ownerMismatch = settings?.hasGitHubApp == true &&
        approvalOwner != null &&
        settings!.gitHubAppOwner?.toLowerCase() != approvalOwner.toLowerCase();

    return KintsugiPanel(
      padding: const EdgeInsets.all(28),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          const SubHeadingTight('GitHub App'),
          if (settings?.hasGitHubApp != true) ...[
            const HintText(
              'Instead of the two tokens below, this server can act as a GitHub App it creates for '
              'itself. The App belongs to the organisation rather than to whoever pasted a token, '
              'its credentials are short-lived and minted here, and the pull requests Sign Script '
              'opens come from the App — so the person who signed a script can approve its pull '
              'request without a branch-protection bypass.',
            ),
            const SizedBox(height: 16),
            LabelledField(
              label: 'Organisation',
              hints: const [
                HintText(
                  'The GitHub organisation to create the App under — normally the one the approval '
                  'repository belongs to. Leave blank to create it under your own account instead, '
                  'which ties it to you.',
                ),
              ],
              child: KintsugiTextField(controller: organization, hintText: 'organisation'),
            ),
            LabelledField(
              label: 'App name',
              hints: const [
                HintText(
                  'Must be unique across all of GitHub, 34 characters at most. Leave blank to use '
                  '"Kintsugi" and this server\'s address.',
                ),
              ],
              child: KintsugiTextField(controller: name, hintText: 'Optional'),
            ),
            const SizedBox(height: 8),
            Align(
              alignment: Alignment.centerLeft,
              child: PrimaryButton(
                label: 'Create GitHub App',
                busy: busy,
                onPressed: busy ? null : onCreate,
              ),
            ),
            const SizedBox(height: 10),
            const HintText(
              'Opens GitHub to confirm the App, then its installation page: choose Only select '
              'repositories and pick the approval repository (and any private repository agent '
              'builds come from). You are brought back here afterwards.',
            ),
          ] else ...[
            HintText(
              settings!.isGitHubAppInstalled
                  ? 'Connected and installed: ${settings.gitHubAppSlug}, owned by '
                      '${settings.gitHubAppOwner}. Both tokens below are minted from it.'
                  : 'Created: ${settings.gitHubAppSlug}, owned by ${settings.gitHubAppOwner} — but '
                      'not installed yet, so the tokens below are still in use. Install it to switch '
                      'over.',
            ),
            if (ownerMismatch) ...[
              const SizedBox(height: 12),
              AlertBox.error(
                'The approval repository belongs to $approvalOwner, but the App is owned by '
                '${settings.gitHubAppOwner}. An installation token can only reach repositories '
                'on the account the App is installed on, so signing will not be able to publish. '
                'Install the App on $approvalOwner, or point the approval repository at a '
                'repository ${settings.gitHubAppOwner} owns.',
              ),
            ],
            const SizedBox(height: 16),
            Wrap(
              spacing: 12,
              runSpacing: 12,
              children: [
                if (installUrl != null)
                  PrimaryButton(
                    label: settings.isGitHubAppInstalled ? 'Change Repositories' : 'Install on GitHub',
                    onPressed: busy ? null : () => locator<PageNavigator>().go(installUrl),
                  ),
                SecondaryButton(
                  label: 'Disconnect',
                  busy: busy,
                  tooltip: 'Forget the App here; the tokens below apply again. The App itself '
                      'stays on GitHub — delete it from the owner\'s Developer settings.',
                  onPressed: busy ? null : onDisconnect,
                ),
              ],
            ),
          ],
        ],
      ),
    );
  }
}

/// Step-by-step help sitting directly under the field it fills in — `.token-help`.
///
/// Inline rather than behind a link because the question "what do I put here" is the only reason
/// this screen is open.
class _TokenHelp extends StatelessWidget {
  const _TokenHelp({required this.steps});

  final List<String> steps;

  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
        decoration: BoxDecoration(
          border: Border(left: BorderSide(color: context.palette.neonDim, width: 2)),
          color: context.palette.accentWash(0.03),
        ),
        child: NumberedSteps([for (final step in steps) HintText(step)]),
      );
}
