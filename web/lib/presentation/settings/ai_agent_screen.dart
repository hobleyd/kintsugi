import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/locator.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/settings.dart';
import '../../domain/repositories/repositories.dart';
import '../../domain/usecases/settings_usecases.dart';
import 'ai_agent_bloc.dart';

/// Which AI provider researches upgrade paths. What `Pages/Settings/AiAgent.cshtml` was.
class AiAgentSettingsScreen extends StatelessWidget {
  const AiAgentSettingsScreen({super.key});

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => AiAgentBloc(
          getSettings: locator<GetAiAgentSettings>(),
          updateSettings: locator<UpdateAiAgentSettings>(),
          getOllamaModels: locator<GetOllamaModels>(),
          checkGooseCliStatus: locator<CheckGooseCliStatus>(),
          checkClaudeAgentSdkStatus: locator<CheckClaudeAgentSdkStatus>(),
        )..add(const AiAgentSettingsRequested()),
        child: const _AiAgentForm(),
      );
}

class _AiAgentForm extends StatefulWidget {
  const _AiAgentForm();

  @override
  State<_AiAgentForm> createState() => _AiAgentFormState();
}

class _AiAgentFormState extends State<_AiAgentForm> {
  final _apiKey = TextEditingController();
  final _baseUrl = TextEditingController();
  final _model = TextEditingController();

  AiProvider _provider = AiProvider.anthropic;
  bool _isEnabled = false;

  @override
  void dispose() {
    _apiKey.dispose();
    _baseUrl.dispose();
    _model.dispose();
    super.dispose();
  }

  void _hydrate(AiAgentSettings settings) {
    _baseUrl.text = settings.baseUrl ?? '';
    _model.text = settings.model ?? '';
    // Never repopulated: the key was never sent here, so there is nothing to put back.
    _apiKey.clear();
    setState(() {
      _provider = settings.provider;
      _isEnabled = settings.isEnabled;
    });
  }

  void _edited() => context.read<AiAgentBloc>().add(const AiAgentSettingsEdited());

  void _probe() {
    final bloc = context.read<AiAgentBloc>();
    if (_provider == AiProvider.ollama) {
      bloc.add(OllamaModelsRequested(_baseUrl.text));
    } else if (_provider == AiProvider.gooseCli) {
      bloc.add(GooseCliStatusRequested(_baseUrl.text));
    } else if (_provider == AiProvider.claudeAgentSdk) {
      bloc.add(const ClaudeAgentSdkStatusRequested());
    }
  }

  void _save() => context.read<AiAgentBloc>().add(AiAgentSettingsSaveRequested(AiAgentSettingsUpdate(
        provider: _provider,
        // Blank means "keep the stored key", which is why an empty field is sent as null rather
        // than as an empty string. Trimmed for the same reason the other fields are, but the
        // consequence here is sharper: this value is handed to the `claude` subprocess as
        // CLAUDE_CODE_OAUTH_TOKEN, and a token pasted with a trailing newline authenticates as a
        // different string — reported as "401 OAuth access token is invalid", which reads exactly
        // like a wrong or expired token. See ClaudeAgentSdkClient.
        apiKey: _apiKey.text.trim().isEmpty ? null : _apiKey.text.trim(),
        baseUrl: _baseUrl.text.trim().isEmpty ? null : _baseUrl.text.trim(),
        model: _model.text.trim().isEmpty ? null : _model.text.trim(),
        isEnabled: _isEnabled,
      )));

  @override
  Widget build(BuildContext context) => BlocConsumer<AiAgentBloc, AiAgentState>(
        listenWhen: (previous, current) => previous.value != current.value && current.value != null,
        listener: (context, state) => _hydrate(state.value!),
        builder: (context, state) {
          final isOllama = _provider == AiProvider.ollama;
          final isGoose = _provider == AiProvider.gooseCli;
          final isClaudeAgentSdk = _provider == AiProvider.claudeAgentSdk;
          // "Cloud" here means a hosted API called with a metered key. The Claude Agent SDK also
          // needs a stored credential, but it is a subscription's OAuth token rather than an API
          // key, so it gets its own field rather than borrowing this one's wording.
          final isCloud = !isOllama && !isGoose && !isClaudeAgentSdk;

          return PageScaffold(
            title: 'AI Agent',
            subtitle: 'Connect the patching system to an AI agent to power automated triage and '
                'remediation suggestions.',
            children: [
              if (state.error != null) AlertBox.error(state.error!),
              if (state.saved) const AlertBox.success('Settings saved.'),
              SettingsFormPanel(
                children: [
                  LabelledField(
                    label: 'Provider',
                    child: KintsugiDropdown<AiProvider>(
                      value: _provider,
                      items: AiProvider.values,
                      labelOf: (value) => value.label,
                      onChanged: (value) {
                        setState(() => _provider = value);
                        _edited();
                        // Probing on selection rather than waiting to be asked: for Ollama the
                        // model field is unusable until the list arrives, and for Goose the whole
                        // question is whether the server can reach the endpoint at all.
                        _probe();
                      },
                    ),
                  ),
                  if (isCloud)
                    LabelledField(
                      label: 'API Key',
                      hints: [
                        if (state.value?.hasApiKey == true)
                          const HintText('An API key is already configured. Leave blank to keep it.'),
                      ],
                      child: KintsugiTextField(
                        controller: _apiKey,
                        obscureText: true,
                        hintText: state.value?.hasApiKey == true
                            ? '•••••••••••• (leave blank to keep current key)'
                            : 'sk-...',
                        errorText: state.errorFor('ApiKey'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                  if (isClaudeAgentSdk)
                    LabelledField(
                      label: 'OAuth Token',
                      hints: [
                        const HintText(
                          'Run `claude setup-token` on a machine signed in to the Claude '
                          'subscription this server should use, and paste the one-year token it '
                          'prints. Research runs then bill that subscription instead of metered '
                          'API credits — a Pro, Max, Team or Enterprise plan is required.',
                        ),
                        if (state.value?.hasApiKey == true)
                          const HintText('A token is already configured. Leave blank to keep it.'),
                      ],
                      child: KintsugiTextField(
                        controller: _apiKey,
                        obscureText: true,
                        hintText: state.value?.hasApiKey == true
                            ? '•••••••••••• (leave blank to keep current token)'
                            : 'sk-ant-oat01-...',
                        errorText: state.errorFor('ApiKey'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                  if (isClaudeAgentSdk)
                    LabelledField(
                      label: 'Server status',
                      hints: [
                        if (state.probeMessage != null) HintText(state.probeMessage!),
                        const HintText(
                          'Checks the saved token, not the one typed above — save first, then '
                          'check. The check makes one real request, so it takes a few seconds.',
                        ),
                      ],
                      child: Align(
                        alignment: Alignment.centerLeft,
                        child: SecondaryButton(
                          label: 'Check',
                          onPressed: state.probing ? null : _probe,
                        ),
                      ),
                    ),
                  if (isOllama || isGoose)
                    LabelledField(
                      label: 'Endpoint URL',
                      hints: [
                        if (state.probeMessage != null) HintText(state.probeMessage!),
                        if (isGoose)
                          const HintText(
                            'The base URL of a `goose serve` instance reachable from this server. '
                            "Leave blank to use Goose's own default local address.",
                          ),
                      ],
                      child: Row(
                        children: [
                          Expanded(
                            child: KintsugiTextField(
                              controller: _baseUrl,
                              hintText: isGoose
                                  ? 'http://127.0.0.1:3284 (leave blank for default)'
                                  : 'http://localhost:11434',
                              errorText: state.errorFor('BaseUrl'),
                              onChanged: (_) => _edited(),
                              onEditingComplete: _probe,
                            ),
                          ),
                          const SizedBox(width: 10),
                          SecondaryButton(
                            label: isGoose ? 'Check' : 'Refresh',
                            onPressed: state.probing ? null : _probe,
                          ),
                        ],
                      ),
                    ),
                  LabelledField(
                    label: 'Model',
                    hints: [
                      if (isGoose)
                        const HintText("Leave blank to use Goose's currently configured model."),
                      if (isClaudeAgentSdk)
                        const HintText(
                          'An alias (opus, sonnet, haiku) or a full model id. Leave blank to use '
                          'whichever model the Claude Code CLI defaults to.',
                        ),
                    ],
                    child: isOllama
                        ? _OllamaModelPicker(
                            models: state.ollamaModels,
                            selected: _model.text,
                            onChanged: (value) {
                              setState(() => _model.text = value);
                              _edited();
                            },
                          )
                        : KintsugiTextField(
                            controller: _model,
                            hintText: 'e.g. claude-opus-4-6, gpt-5',
                            errorText: state.errorFor('Model'),
                            onChanged: (_) => _edited(),
                          ),
                  ),
                  KintsugiCheckbox(
                    label: 'Enable AI agent integration',
                    value: _isEnabled,
                    onChanged: (value) {
                      setState(() => _isEnabled = value);
                      _edited();
                    },
                  ),
                  Align(
                    alignment: Alignment.centerLeft,
                    child: PrimaryButton(
                      label: 'Save Settings',
                      busy: state.saving,
                      onPressed: state.loading ? null : _save,
                    ),
                  ),
                ],
              ),
            ],
          );
        },
      );
}

/// The model field for Ollama: a list of what the endpoint is actually serving.
///
/// The stored value is kept in the list even when the endpoint does not offer it, so a model that
/// has been pulled off the server is still visible as the configured one rather than silently
/// reverting to whichever happens to be first.
class _OllamaModelPicker extends StatelessWidget {
  const _OllamaModelPicker({required this.models, required this.selected, required this.onChanged});

  final List<String> models;
  final String selected;
  final ValueChanged<String> onChanged;

  @override
  Widget build(BuildContext context) {
    final options = [
      if (selected.isNotEmpty && !models.contains(selected)) selected,
      ...models,
    ];

    if (options.isEmpty) {
      return const HintText('No models found — check the endpoint above.');
    }

    return KintsugiDropdown<String>(
      value: selected.isEmpty ? options.first : selected,
      items: options,
      onChanged: onChanged,
    );
  }
}
