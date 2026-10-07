import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/settings.dart';
import 'ai_routing_bloc.dart';

/// Routed mode: web search, connections, and which connection and model each feature runs on.
///
/// Everything here saves on its own button rather than through the AI Agent form's Save, because
/// each piece is independently useful — a connection can be added and tested before anything is
/// routed to it.
class AiRoutingPanel extends StatelessWidget {
  const AiRoutingPanel({super.key, required this.settings});

  /// The AI Agent bloc's copy of the settings row, for the stored web-search choice.
  final AiAgentSettings? settings;

  @override
  Widget build(BuildContext context) => BlocBuilder<AiRoutingBloc, AiRoutingState>(
        builder: (context, state) {
          final routing = state.routing;
          final webSearch = state.webSearch ?? settings;
          return Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (state.error != null) AlertBox.error(state.error!),
              if (state.notice != null) AlertBox.success(state.notice!),
              if (routing != null && routing.routeFor(AiFeature.scriptResearch) == null)
                const AlertBox.info(
                  'Nothing is routed to script research yet, so Routed mode is not configured and no '
                  'research will run. Add a connection, then route script research to it below.',
                ),
              const SizedBox(height: 12),
              _Section(
                title: '1. Connections',
                subtitle: 'Each is one way of reaching models: a protocol, an endpoint and a credential. '
                    'Pick a provider from the models.dev catalog (${state.catalog.providers.length} '
                    'reachable${state.catalog.isStale ? ', cached copy' : ''}) or enter a custom endpoint '
                    '— LiteLLM, vLLM, OpenRouter, Azure, Vertex AI\'s OpenAI-compatible endpoint, anything '
                    'speaking one of the protocols.',
                trailing: SecondaryButton(
                  label: 'Refresh catalog',
                  onPressed: state.busy ? null : () => context.read<AiRoutingBloc>().add(const AiCatalogRefreshRequested()),
                ),
                child: _Connections(routing: routing, catalog: state.catalog, busy: state.busy),
              ),
              const SizedBox(height: 20),
              _Section(
                title: '2. Routes',
                subtitle: 'Which connection and model each feature runs on. Script repair and CPE '
                    'suggestion are cheap, search-free calls, so a smaller model is usually right for them.',
                child: routing == null
                    ? const HintText('Loading…')
                    : Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          for (final feature in AiFeature.values)
                            _RouteRow(
                              key: ValueKey(feature),
                              feature: feature,
                              routing: routing,
                              catalog: state.catalog,
                              busy: state.busy,
                              testing: state.testing.contains(feature),
                              test: state.tests[feature],
                            ),
                        ],
                      ),
              ),
              const SizedBox(height: 20),
              _Section(
                title: '3. Web search',
                subtitle: 'For a model without hosted search — anything OpenAI-compatible, Ollama, or a '
                    'connection with hosted search turned off — research runs on Kintsugi\'s own '
                    'web_search and web_fetch tools, backed by this. web_fetch refuses private, '
                    'loopback, link-local and metadata addresses whatever the model asks for.',
                child: _WebSearchForm(settings: webSearch, busy: state.busy),
              ),
            ],
          );
        },
      );
}

class _Section extends StatelessWidget {
  const _Section({required this.title, required this.subtitle, required this.child, this.trailing});

  final String title;
  final String subtitle;
  final Widget child;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) => KintsugiPanel(
        padding: const EdgeInsets.all(24),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(children: [Expanded(child: SubHeadingTight(title)), ?trailing]),
            HintText(subtitle),
            const SizedBox(height: 16),
            child,
          ],
        ),
      );
}

class _Connections extends StatefulWidget {
  const _Connections({required this.routing, required this.catalog, required this.busy});

  final AiRouting? routing;
  final AiCatalog catalog;
  final bool busy;

  @override
  State<_Connections> createState() => _ConnectionsState();
}

class _ConnectionsState extends State<_Connections> {
  /// The connection being edited: null for none, an empty id for a new one.
  String? _editing;

  @override
  Widget build(BuildContext context) {
    final connections = widget.routing?.connections ?? const [];
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (connections.isEmpty && _editing == null) const HintText('No connections yet.'),
        for (final connection in connections)
          _editing == connection.id
              ? _ConnectionEditor(
                  existing: connection,
                  catalog: widget.catalog,
                  busy: widget.busy,
                  onDone: () => setState(() => _editing = null),
                )
              : _ConnectionTile(
                  connection: connection,
                  busy: widget.busy,
                  onEdit: () => setState(() => _editing = connection.id),
                ),
        const SizedBox(height: 12),
        if (_editing == '')
          _ConnectionEditor(existing: null, catalog: widget.catalog, busy: widget.busy, onDone: () => setState(() => _editing = null))
        else
          Align(
            alignment: Alignment.centerLeft,
            child: SecondaryButton(label: 'Add connection', onPressed: _editing == null ? () => setState(() => _editing = '') : null),
          ),
      ],
    );
  }
}

class _ConnectionTile extends StatelessWidget {
  const _ConnectionTile({required this.connection, required this.busy, required this.onEdit});

  final AiConnection connection;
  final bool busy;
  final VoidCallback onEdit;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(bottom: 10),
        child: Row(
          children: [
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(connection.name, style: Theme.of(context).textTheme.titleSmall),
                  HintText(
                    '${connection.protocol.label} · ${connection.endpointLabel} · ${connection.authMode.label}'
                    '${connection.authMode == AiAuthMode.apiKey ? (connection.hasApiKey ? ' (stored)' : ' (missing)') : ''}'
                    '${connection.useHostedWebSearch ? ' · hosted search' : ''}',
                  ),
                ],
              ),
            ),
            SecondaryButton(label: 'Edit', onPressed: busy ? null : onEdit),
            const SizedBox(width: 8),
            SecondaryButton(
              label: 'Delete',
              onPressed: busy ? null : () => context.read<AiRoutingBloc>().add(AiConnectionDeleteRequested(connection.id)),
            ),
          ],
        ),
      );
}

class _ConnectionEditor extends StatefulWidget {
  const _ConnectionEditor({required this.existing, required this.catalog, required this.busy, required this.onDone});

  final AiConnection? existing;
  final AiCatalog catalog;
  final bool busy;
  final VoidCallback onDone;

  @override
  State<_ConnectionEditor> createState() => _ConnectionEditorState();
}

class _ConnectionEditorState extends State<_ConnectionEditor> {
  static const _custom = '';

  final _name = TextEditingController();
  final _baseUrl = TextEditingController();
  final _apiKey = TextEditingController();
  final _project = TextEditingController();
  final _location = TextEditingController();

  String _source = _custom;
  AiWireProtocol _protocol = AiWireProtocol.openAiChatCompletions;
  AiAuthMode _auth = AiAuthMode.apiKey;
  bool _hosted = false;

  @override
  void initState() {
    super.initState();
    final existing = widget.existing;
    if (existing != null) {
      _name.text = existing.name;
      _baseUrl.text = existing.baseUrl ?? '';
      _project.text = existing.googleCloudProject ?? '';
      _location.text = existing.googleCloudLocation ?? '';
      _source = widget.catalog.provider(existing.catalogProviderId) != null ? existing.catalogProviderId! : _custom;
      _protocol = existing.protocol;
      _auth = existing.authMode;
      _hosted = existing.useHostedWebSearch;
    }
  }

  @override
  void dispose() {
    for (final c in [_name, _baseUrl, _apiKey, _project, _location]) {
      c.dispose();
    }
    super.dispose();
  }

  /// Prefills from the catalog; everything stays editable, since the catalog is a starting point.
  void _pickSource(String id) {
    final provider = widget.catalog.provider(id);
    setState(() {
      _source = id;
      if (provider == null) return;
      _name.text = provider.name;
      _protocol = provider.protocol;
      _baseUrl.text = provider.defaultBaseUrl ?? '';
      _auth = provider.usesGoogleCloud
          ? AiAuthMode.googleCloud
          : provider.requiresApiKey
              ? AiAuthMode.apiKey
              : AiAuthMode.none;
      _hosted = provider.protocol.hasHostedSearch;
      if (provider.usesGoogleCloud && _location.text.isEmpty) _location.text = 'global';
    });
  }

  String? _blankToNull(TextEditingController c) => c.text.trim().isEmpty ? null : c.text.trim();

  void _save() {
    context.read<AiRoutingBloc>().add(AiConnectionSaveRequested(AiConnectionDraft(
          id: widget.existing?.id,
          name: _name.text.trim(),
          catalogProviderId: _source == _custom ? null : _source,
          protocol: _protocol,
          baseUrl: _blankToNull(_baseUrl),
          authMode: _auth,
          apiKey: _blankToNull(_apiKey),
          googleCloudProject: _blankToNull(_project),
          googleCloudLocation: _blankToNull(_location),
          useHostedWebSearch: _hosted && _protocol.hasHostedSearch,
        )));
    widget.onDone();
  }

  @override
  Widget build(BuildContext context) {
    final sources = [_custom, ...widget.catalog.providers.map((p) => p.id)];
    final isVertexNative = _auth == AiAuthMode.googleCloud && _protocol != AiWireProtocol.openAiChatCompletions;

    return KintsugiPanel(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          LabelledField(
            label: 'Provider',
            child: KintsugiDropdown<String>(
              value: sources.contains(_source) ? _source : _custom,
              items: sources,
              labelOf: (id) => id == _custom ? 'Custom endpoint' : widget.catalog.provider(id)?.name ?? id,
              onChanged: _pickSource,
            ),
          ),
          LabelledField(label: 'Name', child: KintsugiTextField(controller: _name, hintText: 'e.g. Vertex (global)')),
          LabelledField(
            label: 'Protocol',
            child: KintsugiDropdown<AiWireProtocol>(
              value: _protocol,
              items: AiWireProtocol.values,
              labelOf: (p) => p.label,
              onChanged: (p) => setState(() {
                _protocol = p;
                _hosted = _hosted && p.hasHostedSearch;
              }),
            ),
          ),
          LabelledField(
            label: 'Authentication',
            child: KintsugiDropdown<AiAuthMode>(
              value: _auth,
              items: AiAuthMode.values,
              labelOf: (a) => a.label,
              onChanged: (a) => setState(() => _auth = a),
            ),
          ),
          if (!isVertexNative)
            LabelledField(
              label: 'Base URL',
              hints: [
                HintText(_auth == AiAuthMode.googleCloud
                    ? 'The full Vertex AI OpenAI-compatible endpoint, e.g. https://aiplatform.googleapis.com/v1/projects/<project>/locations/global/endpoints/openapi'
                    : 'Blank uses the protocol\'s default host (api.openai.com, api.anthropic.com, Google AI Studio).'),
              ],
              child: KintsugiTextField(controller: _baseUrl, hintText: 'https://…/v1'),
            ),
          if (_auth == AiAuthMode.apiKey)
            LabelledField(
              label: 'API key',
              child: KintsugiTextField(
                controller: _apiKey,
                obscureText: true,
                hintText: widget.existing?.hasApiKey == true ? 'Stored — leave blank to keep it' : 'Key',
              ),
            ),
          if (isVertexNative) ...[
            LabelledField(
              label: 'Google Cloud project',
              child: KintsugiTextField(controller: _project, hintText: 'my-project-123'),
            ),
            LabelledField(
              label: 'Location',
              hints: const [
                HintText('Where inference runs — a data-residency decision. e.g. australia-southeast1, us-east5, or global. '
                    'This server authenticates as its own Google service account (Workload Identity on GKE), which needs '
                    'roles/aiplatform.user; Claude models must first be enabled in Vertex Model Garden.'),
              ],
              child: KintsugiTextField(controller: _location, hintText: 'global'),
            ),
          ],
          if (_protocol.hasHostedSearch)
            KintsugiCheckbox(
              label: 'Use the provider\'s own web search for research (turn off to use Kintsugi\'s tools instead)',
              value: _hosted,
              onChanged: (v) => setState(() => _hosted = v),
            ),
          const SizedBox(height: 8),
          Row(
            children: [
              PrimaryButton(label: 'Save connection', busy: widget.busy, onPressed: widget.busy ? null : _save),
              const SizedBox(width: 10),
              SecondaryButton(label: 'Cancel', onPressed: widget.onDone),
            ],
          ),
        ],
      ),
    );
  }
}

class _RouteRow extends StatefulWidget {
  const _RouteRow({
    super.key,
    required this.feature,
    required this.routing,
    required this.catalog,
    required this.busy,
    required this.testing,
    required this.test,
  });

  final AiFeature feature;
  final AiRouting routing;
  final AiCatalog catalog;
  final bool busy;
  final bool testing;
  final AiConnectionTestResult? test;

  @override
  State<_RouteRow> createState() => _RouteRowState();
}

class _RouteRowState extends State<_RouteRow> {
  final _model = TextEditingController();
  String? _connectionId;

  @override
  void initState() {
    super.initState();
    _hydrate();
  }

  @override
  void didUpdateWidget(covariant _RouteRow oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.routing.routeFor(widget.feature) != widget.routing.routeFor(widget.feature)) {
      _hydrate();
    }
  }

  void _hydrate() {
    final own = widget.routing.routeFor(widget.feature);
    _connectionId = own?.connectionId ?? widget.routing.connections.firstOrNull?.id;
    _model.text = own?.model ?? '';
  }

  @override
  void dispose() {
    _model.dispose();
    super.dispose();
  }

  /// The catalog's models for the selected connection's provider, narrowed to the ones that speak
  /// the connection's protocol — Vertex lists Claude and Gemini side by side.
  List<AiCatalogModel> _catalogModels(AiConnection? connection) {
    final provider = widget.catalog.provider(connection?.catalogProviderId);
    if (provider == null || connection == null) return const [];
    return provider.models.where((m) => m.protocol == connection.protocol).toList();
  }

  @override
  Widget build(BuildContext context) {
    final connections = widget.routing.connections;
    final own = widget.routing.routeFor(widget.feature);
    final effective = widget.routing.effectiveRouteFor(widget.feature);
    final selected = _connectionId == null ? null : widget.routing.connection(_connectionId!);
    final models = _catalogModels(selected);
    final test = widget.test;

    return Padding(
      padding: const EdgeInsets.only(bottom: 18),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(widget.feature.label, style: Theme.of(context).textTheme.titleSmall),
          HintText(widget.feature.description),
          if (own == null && effective != null)
            HintText('Not routed itself — falls back to ${widget.routing.connection(effective.connectionId)?.name ?? '?'} · ${effective.model}.'),
          const SizedBox(height: 8),
          if (connections.isEmpty)
            const HintText('Add a connection first.')
          else
            Wrap(
              spacing: 10,
              runSpacing: 10,
              crossAxisAlignment: WrapCrossAlignment.center,
              children: [
                SizedBox(
                  width: 260,
                  child: KintsugiDropdown<String>(
                    value: connections.any((c) => c.id == _connectionId) ? _connectionId! : connections.first.id,
                    items: [for (final c in connections) c.id],
                    labelOf: (id) => widget.routing.connection(id)?.name ?? id,
                    onChanged: (id) => setState(() => _connectionId = id),
                  ),
                ),
                SizedBox(width: 280, child: KintsugiTextField(controller: _model, hintText: 'model id')),
                if (models.isNotEmpty)
                  SizedBox(
                    width: 280,
                    child: KintsugiDropdown<String>(
                      value: '',
                      items: ['', ...models.map((m) => m.id)],
                      labelOf: (id) {
                        if (id.isEmpty) return 'Pick from catalog (${models.length})…';
                        final m = models.firstWhere((m) => m.id == id);
                        return '${m.name}${m.toolCall ? '' : ' (no tool calls)'}';
                      },
                      onChanged: (id) {
                        if (id.isNotEmpty) setState(() => _model.text = id);
                      },
                    ),
                  ),
                PrimaryButton(
                  label: 'Save route',
                  onPressed: widget.busy || _model.text.trim().isEmpty
                      ? null
                      : () => context.read<AiRoutingBloc>().add(AiRouteSaveRequested(
                            widget.feature,
                            connections.any((c) => c.id == _connectionId) ? _connectionId! : connections.first.id,
                            _model.text.trim(),
                          )),
                ),
                if (effective != null)
                  SecondaryButton(
                    label: widget.testing ? 'Testing…' : 'Test',
                    onPressed: widget.testing
                        ? null
                        : () => context.read<AiRoutingBloc>().add(AiRouteTestRequested(widget.feature, effective.connectionId, effective.model)),
                  ),
                if (own != null)
                  SecondaryButton(
                    label: 'Clear',
                    onPressed: widget.busy ? null : () => context.read<AiRoutingBloc>().add(AiRouteClearRequested(widget.feature)),
                  ),
              ],
            ),
          if (test != null) ...[
            const SizedBox(height: 6),
            test.succeeded
                ? HintText('Test passed in ${test.elapsedMilliseconds} ms — the model replied: "${test.reply}"')
                : AlertBox.error('Test failed: ${test.error}'),
          ],
        ],
      ),
    );
  }
}

class _WebSearchForm extends StatefulWidget {
  const _WebSearchForm({required this.settings, required this.busy});

  final AiAgentSettings? settings;
  final bool busy;

  @override
  State<_WebSearchForm> createState() => _WebSearchFormState();
}

class _WebSearchFormState extends State<_WebSearchForm> {
  final _apiKey = TextEditingController();
  final _baseUrl = TextEditingController();
  WebSearchBackend _backend = WebSearchBackend.none;

  @override
  void initState() {
    super.initState();
    _hydrate();
  }

  @override
  void didUpdateWidget(covariant _WebSearchForm oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.settings != widget.settings) _hydrate();
  }

  void _hydrate() {
    _backend = widget.settings?.webSearchBackend ?? WebSearchBackend.none;
    _baseUrl.text = widget.settings?.webSearchBaseUrl ?? '';
    _apiKey.clear();
  }

  @override
  void dispose() {
    _apiKey.dispose();
    _baseUrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          LabelledField(
            label: 'Backend',
            child: KintsugiDropdown<WebSearchBackend>(
              value: _backend,
              items: WebSearchBackend.values,
              labelOf: (b) => b.label,
              onChanged: (b) => setState(() => _backend = b),
            ),
          ),
          if (_backend.needsKey)
            LabelledField(
              label: 'API key',
              child: KintsugiTextField(
                controller: _apiKey,
                obscureText: true,
                hintText: widget.settings?.hasWebSearchApiKey == true ? 'Stored — leave blank to keep it' : 'Key',
              ),
            ),
          if (_backend == WebSearchBackend.searXng)
            LabelledField(label: 'SearXNG base URL', child: KintsugiTextField(controller: _baseUrl, hintText: 'https://searx.internal')),
          Align(
            alignment: Alignment.centerLeft,
            child: PrimaryButton(
              label: 'Save web search',
              busy: widget.busy,
              onPressed: widget.busy
                  ? null
                  : () => context.read<AiRoutingBloc>().add(AiWebSearchSaveRequested(
                        backend: _backend,
                        apiKey: _apiKey.text.trim().isEmpty ? null : _apiKey.text.trim(),
                        clearApiKey: false,
                        baseUrl: _baseUrl.text.trim().isEmpty ? null : _baseUrl.text.trim(),
                      )),
            ),
          ),
        ],
      );
}
