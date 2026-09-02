import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/injection.dart';
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
  const GitHubSettingsScreen({super.key});

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => GitHubSettingsBloc(
          getSettings: locator<GetGitHubSettings>(),
          updateSettings: locator<UpdateGitHubSettings>(),
        )..add(const GitHubSettingsRequested()),
        child: const _GitHubForm(),
      );
}

class _GitHubForm extends StatefulWidget {
  const _GitHubForm();

  @override
  State<_GitHubForm> createState() => _GitHubFormState();
}

class _GitHubFormState extends State<_GitHubForm> {
  final _agentPackageRepository = TextEditingController();
  final _scriptApprovalRepository = TextEditingController();
  final _apiToken = TextEditingController();
  final _scriptApprovalToken = TextEditingController();

  bool _clearApiToken = false;
  bool _clearScriptApprovalToken = false;

  @override
  void dispose() {
    _agentPackageRepository.dispose();
    _scriptApprovalRepository.dispose();
    _apiToken.dispose();
    _scriptApprovalToken.dispose();
    super.dispose();
  }

  void _hydrate(GitHubSettings settings) {
    // The effective values, defaults included, rather than blanks — the operator should see which
    // repositories this server is actually pointed at.
    _agentPackageRepository.text = settings.agentPackageRepository;
    _scriptApprovalRepository.text = settings.scriptApprovalRepository;

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
              if (state.error != null) AlertBox.error(state.error!),
              if (state.saved) const AlertBox.success('GitHub settings saved.'),
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
