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
import '../../domain/usecases/settings_usecases.dart';
import 'auditing_bloc.dart';
import 'settings_state.dart';

/// Where audit events are shipped: a logging platform, chosen from a list, and the fields that
/// platform needs to be reached.
///
/// The same shape as `AuthenticationSettingsScreen`, deliberately — a provider dropdown, the
/// fields it reads, and instructions for exactly that provider beside them. Which fields those
/// are is decided on the server (`AuditSettings` and `UpdateAuditSettingsCommandValidator`); this
/// screen only decides which boxes to show, and the validator's per-field messages land under the
/// matching box by C# property name.
class AuditingSettingsScreen extends StatelessWidget {
  const AuditingSettingsScreen({super.key});

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => AuditingSettingsBloc(
          getSettings: locator<GetAuditSettings>(),
          updateSettings: locator<UpdateAuditSettings>(),
        )..add(const AuditingSettingsRequested()),
        child: const _AuditingForm(),
      );
}

class _AuditingForm extends StatefulWidget {
  const _AuditingForm();

  @override
  State<_AuditingForm> createState() => _AuditingFormState();
}

class _AuditingFormState extends State<_AuditingForm> {
  final _endpoint = TextEditingController();
  final _region = TextEditingController();
  final _clientId = TextEditingController();
  final _secret = TextEditingController();
  final _tenantId = TextEditingController();
  final _projectId = TextEditingController();
  final _logGroup = TextEditingController();
  final _dataCollectionRuleId = TextEditingController();
  final _stream = TextEditingController();
  final _index = TextEditingController();

  AuditProvider _provider = AuditProvider.datadog;
  bool _isEnabled = false;
  bool _clearSecret = false;

  @override
  void dispose() {
    _endpoint.dispose();
    _region.dispose();
    _clientId.dispose();
    _secret.dispose();
    _tenantId.dispose();
    _projectId.dispose();
    _logGroup.dispose();
    _dataCollectionRuleId.dispose();
    _stream.dispose();
    _index.dispose();
    super.dispose();
  }

  void _hydrate(AuditSettings settings) {
    _endpoint.text = settings.endpoint ?? '';
    _region.text = settings.region ?? '';
    _clientId.text = settings.clientId ?? '';
    _tenantId.text = settings.tenantId ?? '';
    _projectId.text = settings.projectId ?? '';
    _logGroup.text = settings.logGroup ?? '';
    _dataCollectionRuleId.text = settings.dataCollectionRuleId ?? '';
    _stream.text = settings.stream ?? '';
    _index.text = settings.index ?? '';
    // Never repopulated, even after a save: the value was never sent here, and echoing a
    // submitted one back would leave it sitting in the form.
    _secret.clear();
    setState(() {
      _provider = settings.provider;
      _isEnabled = settings.isEnabled;
      _clearSecret = false;
    });
  }

  void _edited() => context.read<AuditingSettingsBloc>().add(const AuditingSettingsEdited());

  void _save() => context.read<AuditingSettingsBloc>().add(AuditingSettingsSaveRequested(
        provider: _provider,
        isEnabled: _isEnabled,
        endpoint: _endpoint.text.trim(),
        region: _region.text.trim(),
        clientId: _clientId.text.trim(),
        secret: _secret.text.isEmpty ? null : _secret.text,
        clearSecret: _clearSecret,
        tenantId: _tenantId.text.trim(),
        projectId: _projectId.text.trim(),
        logGroup: _logGroup.text.trim(),
        dataCollectionRuleId: _dataCollectionRuleId.text.trim(),
        stream: _stream.text.trim(),
        index: _index.text.trim(),
      ));

  /// Whether the stored secret still applies to the provider currently selected. The server drops
  /// it on a provider change, so a stored-for-Datadog secret is no comfort when AWS is chosen.
  bool _hasSecretForSelection(SettingsState<AuditSettings> state) =>
      state.value?.hasSecret == true && state.value?.provider == _provider;

  Widget _text(
    TextEditingController controller,
    SettingsState<AuditSettings> state,
    String property, {
    String? hintText,
  }) =>
      KintsugiTextField(
        controller: controller,
        hintText: hintText,
        errorText: state.errorFor(property),
        onChanged: (_) => _edited(),
      );

  @override
  Widget build(BuildContext context) =>
      BlocConsumer<AuditingSettingsBloc, SettingsState<AuditSettings>>(
        listenWhen: (previous, current) =>
            previous.value != current.value && current.value != null,
        listener: (context, state) => _hydrate(state.value!),
        builder: (context, state) => PageScaffold(
          title: 'Auditing',
          subtitle: 'Ship a record of what happens here — sign-ins, scripts signed and adopted, '
              'settings changed, remote control requested — to Datadog, Grafana, Google Cloud, AWS, '
              'Azure, Splunk or any HTTP endpoint that takes JSON.',
          children: [
            if (state.error != null) AlertBox.error(state.error!),
            if (state.saved) const AlertBox.success('Settings saved.'),
            SettingsColumns(
              form: SettingsFormPanel(
                maxWidth: double.infinity,
                children: [
                  LabelledField(
                    label: 'Platform',
                    child: KintsugiDropdown<AuditProvider>(
                      value: _provider,
                      items: AuditProvider.values,
                      labelOf: (value) => value.label,
                      onChanged: (value) {
                        setState(() => _provider = value);
                        _edited();
                      },
                    ),
                  ),
                  ..._providerFields(state),
                  _secretField(state),
                  KintsugiCheckbox(
                    label: 'Send audit events to this platform',
                    value: _isEnabled,
                    onChanged: (value) {
                      setState(() => _isEnabled = value);
                      _edited();
                    },
                  ),
                  const HintText(
                    'Leaving this off keeps the configuration and sends nothing, so switching '
                    'auditing off never means deleting a credential.',
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
              aside: _SetupInstructions(provider: _provider),
            ),
          ],
        ),
      );

  /// The non-secret fields the selected provider reads, in the order the instructions mention
  /// them. Property names match the C# command, which is what the validator's messages are keyed
  /// by.
  List<Widget> _providerFields(SettingsState<AuditSettings> state) => switch (_provider) {
        AuditProvider.datadog => [
            LabelledField(
              label: 'Site',
              hints: const [
                HintText(
                  'The Datadog site you sign in at, without the app. prefix. Leave blank for '
                  'datadoghq.com (US1).',
                ),
              ],
              child: _text(_region, state, 'Region', hintText: 'datadoghq.com'),
            ),
          ],
        AuditProvider.grafanaLoki => [
            LabelledField(
              label: 'Push URL',
              hints: const [
                HintText(
                  'The Loki base URL. /loki/api/v1/push is appended — do not include it.',
                ),
              ],
              child: _text(_endpoint, state, 'Endpoint',
                  hintText: 'https://logs-prod-000.grafana.net'),
            ),
            LabelledField(
              label: 'Username (optional)',
              hints: const [
                HintText(
                  'Grafana Cloud: the numeric User shown on the stack\'s Loki details page. Leave '
                  'blank for a self-hosted Loki with no authentication.',
                ),
              ],
              child: _text(_clientId, state, 'ClientId', hintText: '123456'),
            ),
          ],
        AuditProvider.googleCloudLogging => [
            LabelledField(
              label: 'Project ID',
              hints: const [
                HintText('The project ID (not its display name or number).'),
              ],
              child: _text(_projectId, state, 'ProjectId', hintText: 'my-project-123456'),
            ),
          ],
        AuditProvider.awsCloudWatch => [
            LabelledField(
              label: 'Region',
              hints: const [HintText('The region the log group lives in.')],
              child: _text(_region, state, 'Region', hintText: 'ap-southeast-2'),
            ),
            LabelledField(
              label: 'Log group',
              child: _text(_logGroup, state, 'LogGroup', hintText: '/kintsugi/audit'),
            ),
            LabelledField(
              label: 'Log stream (optional)',
              hints: const [
                HintText('Created in the group if it does not exist. Blank names it after this server.'),
              ],
              child: _text(_stream, state, 'Stream'),
            ),
            LabelledField(
              label: 'Access key ID',
              child: _text(_clientId, state, 'ClientId', hintText: 'AKIA…'),
            ),
          ],
        AuditProvider.azureMonitor => [
            LabelledField(
              label: 'Data collection endpoint',
              hints: const [
                HintText('The endpoint\'s Logs Ingestion URL, from its Overview page.'),
              ],
              child: _text(_endpoint, state, 'Endpoint',
                  hintText: 'https://kintsugi-abcd.australiaeast-1.ingest.monitor.azure.com'),
            ),
            LabelledField(
              label: 'Data collection rule ID',
              hints: const [
                HintText('The rule\'s immutableId, from its JSON view — not its name.'),
              ],
              child: _text(_dataCollectionRuleId, state, 'DataCollectionRuleId',
                  hintText: 'dcr-0123456789abcdef0123456789abcdef'),
            ),
            LabelledField(
              label: 'Stream name',
              hints: const [
                HintText('Custom- followed by the table name, as the rule\'s dataFlows declare it.'),
              ],
              child: _text(_stream, state, 'Stream', hintText: 'Custom-KintsugiAudit_CL'),
            ),
            LabelledField(
              label: 'Directory (tenant) ID',
              child: _text(_tenantId, state, 'TenantId'),
            ),
            LabelledField(
              label: 'Application (client) ID',
              child: _text(_clientId, state, 'ClientId'),
            ),
          ],
        AuditProvider.splunkHec => [
            LabelledField(
              label: 'HTTP Event Collector URL',
              hints: const [
                HintText(
                  'The collector\'s base URL. /services/collector/event is appended — do not '
                  'include it.',
                ),
              ],
              child: _text(_endpoint, state, 'Endpoint', hintText: 'https://splunk.example.com:8088'),
            ),
            LabelledField(
              label: 'Index (optional)',
              hints: const [
                HintText('Overrides the token\'s default index. Must be one the token is allowed to write to.'),
              ],
              child: _text(_index, state, 'Index'),
            ),
          ],
        AuditProvider.genericHttp => [
            LabelledField(
              label: 'Endpoint URL',
              hints: const [
                HintText('Events are POSTed here as JSON, one object per request.'),
              ],
              child: _text(_endpoint, state, 'Endpoint', hintText: 'https://logs.example.com/kintsugi'),
            ),
          ],
      };

  Widget _secretField(SettingsState<AuditSettings> state) {
    final hasSecret = _hasSecretForSelection(state);
    final required = _provider.requiresSecret;
    final label = switch (_provider) {
      AuditProvider.datadog => 'API key',
      AuditProvider.grafanaLoki => 'Token (optional)',
      AuditProvider.googleCloudLogging => 'Service account key (JSON)',
      AuditProvider.awsCloudWatch => 'Secret access key',
      AuditProvider.azureMonitor => 'Client secret',
      AuditProvider.splunkHec => 'HEC token',
      AuditProvider.genericHttp => 'Bearer token (optional)',
    };
    final switched = state.value != null && state.value!.provider != _provider;

    return LabelledField(
      label: label,
      hints: [
        if (hasSecret)
          HintText(
            'A ${required ? 'secret' : 'token'} is already configured. Leave blank to keep it.',
          ),
        if (switched && state.value?.hasSecret == true)
          const HintText(
            'Changing the platform drops the stored secret — it was issued by the previous one and '
            'must not be sent to this one. Enter the new platform\'s credential.',
          ),
        if (_provider == AuditProvider.googleCloudLogging)
          const HintText('Paste the entire contents of the downloaded key file.'),
      ],
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          KintsugiTextField(
            controller: _secret,
            obscureText: true,
            hintText: hasSecret ? '•••••••••••• (leave blank to keep current secret)' : '',
            errorText: state.errorFor('Secret'),
            onChanged: (_) => _edited(),
          ),
          if (hasSecret && !required)
            KintsugiCheckbox(
              label: 'Remove the stored token',
              value: _clearSecret,
              onChanged: (value) {
                setState(() => _clearSecret = value);
                _edited();
              },
            ),
        ],
      ),
    );
  }
}

/// Setup instructions for whichever platform is selected.
///
/// These restate decisions that live in code, so keep them in step with it: which fields a
/// platform requires is `UpdateAuditSettingsCommandValidator` and `AuditSettings.Apply`, and the
/// URL paths, header names and label values each platform is sent are the contract the shipping
/// side implements — an operator granting a credential from these steps is entitled to have the
/// events arrive the way they describe.
class _SetupInstructions extends StatelessWidget {
  const _SetupInstructions({required this.provider});

  final AuditProvider provider;

  @override
  Widget build(BuildContext context) => SettingsAside(
        title: 'Setting this up',
        children: [
          const HintText(
            'Events are sent from the API server, not from your browser — so the credential is '
            'used by that server, an allow-list on the platform\'s side needs that server\'s '
            'outbound address, and the api container needs outbound HTTPS to the platform\'s '
            'ingest host.',
          ),
          const SizedBox(height: 8),
          const HintText(
            'Kintsugi only ever writes. Grant the credential permission to ingest logs and nothing '
            'else; none of the steps below need read access.',
          ),
          const SizedBox(height: 20),
          ..._providerSteps(),
        ],
      );

  List<Widget> _providerSteps() => switch (provider) {
        AuditProvider.datadog => [
            const SubHeadingTight('Datadog'),
            const NumberedSteps([
              HintText(
                'In Datadog, open Organization Settings → API Keys and create a key named for this '
                'server. It must be an API key: logs intake authenticates with the DD-API-KEY '
                'header, and an Application key is refused there.',
              ),
              HintText(
                'Read your site off the address you sign in at: app.datadoghq.com is datadoghq.com, '
                'app.datadoghq.eu is datadoghq.eu, and us3.datadoghq.com, us5.datadoghq.com, '
                'ap1.datadoghq.com and ddog-gov.com are themselves. Enter it, or leave it blank for '
                'datadoghq.com.',
              ),
              HintText('Paste the key into API key.'),
            ]),
            const SizedBox(height: 8),
            const HintText('Events are posted to the site\'s logs intake:'),
            const SizedBox(height: 4),
            const CodeText('https://http-intake.logs.<site>/api/v2/logs'),
            const SizedBox(height: 8),
            const HintText(
              'with source and service both set to kintsugi, so a Logs pipeline or index filter '
              'can pick them out. A key from the wrong site answers 403 with no further detail — '
              'if events never appear, check the site before the key.',
            ),
          ],
        AuditProvider.grafanaLoki => [
            const SubHeadingTight('Grafana Cloud'),
            const NumberedSteps([
              HintText(
                'In grafana.com, open your stack and choose Loki → Details (the "Send Logs" page). '
                'Copy the URL — https://logs-prod-XXX.grafana.net — into Push URL and the numeric '
                'User into Username.',
              ),
              HintText(
                'Under Security → Access Policies, create a policy scoped to this stack with the '
                'logs:write scope only, add a token to it, and paste that token into Token. A '
                'legacy API key with the MetricsPublisher role also works.',
              ),
            ]),
            const SizedBox(height: 12),
            const SubHeadingTight('Self-hosted Loki'),
            const HintText(
              'Push URL is Loki\'s base address, http://loki.internal:3100 for a default install. '
              'Leave Username and Token blank unless a reverse proxy in front asks for basic '
              'authentication, in which case enter that pair; plain http is accepted for exactly '
              'this case.',
            ),
            const SizedBox(height: 8),
            const HintText('Events are pushed to:'),
            const SizedBox(height: 4),
            const CodeText('<push url>/loki/api/v1/push'),
            const SizedBox(height: 8),
            const HintText(
              'as a single stream labelled app="kintsugi", with the event\'s own fields in the '
              'JSON line rather than as labels — Loki indexes labels, and a high-cardinality one '
              'such as the actor would be rejected by a default limits configuration.',
            ),
          ],
        AuditProvider.googleCloudLogging => [
            const SubHeadingTight('Google Cloud Logging'),
            const NumberedSteps([
              HintText(
                'In the Cloud console, confirm the Cloud Logging API is enabled for the project '
                '(APIs & Services → Enabled APIs).',
              ),
              HintText(
                'Open IAM & Admin → Service Accounts and create one for this server. Grant it the '
                'Logs Writer role (roles/logging.logWriter) on the project and nothing else.',
              ),
              HintText(
                'On the service account\'s Keys tab, choose Add key → Create new key → JSON. The '
                'file downloads once; paste its entire contents into Service account key.',
              ),
              HintText(
                'Enter the project ID — the identifier, not the display name or the numeric project '
                'number.',
              ),
            ]),
            const SizedBox(height: 8),
            const HintText('Entries are written with the Logging API to:'),
            const SizedBox(height: 4),
            const CodeText('projects/<project>/logs/kintsugi-audit'),
            const SizedBox(height: 8),
            const HintText(
              'as global resources with the event in jsonPayload. If key creation is refused, the '
              'organisation policy iam.disableServiceAccountKeyCreation is in force and needs an '
              'exception for this project; workload identity federation is not an option here, '
              'because this server runs outside Google Cloud.',
            ),
          ],
        AuditProvider.awsCloudWatch => [
            const SubHeadingTight('AWS CloudWatch Logs'),
            const NumberedSteps([
              HintText(
                'In CloudWatch, open Log groups → Create log group. Name it (/kintsugi/audit is a '
                'sensible choice), set the retention you need, and note the region.',
              ),
              HintText(
                'In IAM, create a user for this server with no console access and attach an inline '
                'policy allowing logs:CreateLogStream, logs:DescribeLogStreams and logs:PutLogEvents '
                'on that group\'s ARN and its streams — '
                'arn:aws:logs:<region>:<account>:log-group:/kintsugi/audit:* — and nothing else.',
              ),
              HintText(
                'On the user\'s Security credentials tab, create an access key for an application '
                'running outside AWS. Paste the access key ID and secret access key into the form; '
                'the secret is shown once.',
              ),
              HintText(
                'Leave Log stream blank to have one created and named after this server, or name '
                'one; a named stream that does not exist is created too.',
              ),
            ]),
            const SizedBox(height: 8),
            const HintText(
              'Events are written with PutLogEvents, signed with SigV4 for the region above. This '
              'is a long-lived access key rather than a role, because the API server runs outside '
              'AWS — rotate it on the schedule your IAM policy expects, by pasting the new secret '
              'here.',
            ),
          ],
        AuditProvider.azureMonitor => [
            const SubHeadingTight('Azure Monitor (Log Analytics)'),
            const HintText(
              'Events go in through the Logs Ingestion API, which needs a table to land in, a rule '
              'and endpoint to route through, and an Entra application to authenticate as. Five '
              'values result, and each of the steps produces one or two of them.',
            ),
            const SizedBox(height: 8),
            const NumberedSteps([
              HintText(
                'In a Log Analytics workspace, open Tables → Create → New custom log (DCR-based). '
                'Name the table KintsugiAudit (Azure appends _CL). The wizard asks for a data '
                'collection rule and a data collection endpoint — create both in the workspace\'s '
                'region — and for a sample record; give it {"time": "2026-01-01T00:00:00Z", '
                '"event": "sample"} and map time to TimeGenerated.',
              ),
              HintText(
                'Open the data collection endpoint and copy the Logs Ingestion URL from its Overview '
                'into Data collection endpoint.',
              ),
              HintText(
                'Open the data collection rule, choose JSON view, and copy immutableId (dcr-…) into '
                'Data collection rule ID. The stream name is Custom-KintsugiAudit_CL — the '
                'dataFlows[].streams entry in that same JSON.',
              ),
              HintText(
                'In Microsoft Entra, open App registrations → New registration; no redirect URI is '
                'needed. Copy the Application (client) ID and Directory (tenant) ID from its '
                'Overview, then under Certificates & secrets add a client secret and copy its Value '
                '(not its ID) into Client secret.',
              ),
              HintText(
                'Back on the data collection rule, open Access control (IAM) → Add role assignment '
                'and give that application the Monitoring Metrics Publisher role. Allow up to thirty '
                'minutes for the assignment to take effect before expecting events.',
              ),
            ]),
            const SizedBox(height: 8),
            const HintText('Each event is posted to:'),
            const SizedBox(height: 4),
            const CodeText('<endpoint>/dataCollectionRules/<rule id>/streams/<stream>'),
            const SizedBox(height: 8),
            const HintText(
              'with a token obtained by client credentials from '
              'login.microsoftonline.com/<tenant> for the scope https://monitor.azure.com/.default. '
              'A 403 after everything above is almost always the role assignment still propagating; '
              'a 404 is the rule ID (it must be the immutable ID, not the name) or the stream.',
            ),
          ],
        AuditProvider.splunkHec => [
            const SubHeadingTight('Splunk HTTP Event Collector'),
            const NumberedSteps([
              HintText(
                'In Splunk, open Settings → Data Inputs → HTTP Event Collector → Global Settings and '
                'make sure All Tokens is Enabled. Note the port (8088 by default) and whether SSL '
                'is enabled on it.',
              ),
              HintText(
                'Choose New Token. Name it for this server, leave indexer acknowledgement off, pick '
                'the indexes it may write to and a default, and set the source type to _json. '
                'Copy the token value into HEC token.',
              ),
              HintText(
                'Enter the collector\'s base URL. For Splunk Enterprise that is '
                'https://<host>:8088; for Splunk Cloud it is '
                'https://http-inputs-<stack>.splunkcloud.com on port 443, and the http-inputs- '
                'prefix is required.',
              ),
              HintText(
                'Optionally name an index. It must be one of the token\'s allowed indexes; blank '
                'uses the token\'s default.',
              ),
            ]),
            const SizedBox(height: 8),
            const HintText('Events are posted to:'),
            const SizedBox(height: 4),
            const CodeText('<url>/services/collector/event'),
            const SizedBox(height: 8),
            const HintText(
              'with the header Authorization: Splunk <token>, sourcetype _json and source '
              'kintsugi. Indexer acknowledgement must stay off for this token: acknowledgements are '
              'not requested, and a token that requires them rejects every event.',
            ),
          ],
        AuditProvider.genericHttp => [
            const SubHeadingTight('Generic HTTP endpoint'),
            const HintText(
              'For anything not listed above: a Vector or Fluent Bit HTTP source, a Logstash http '
              'input, an OpenTelemetry collector\'s HTTP receiver behind a small transform, or an '
              'endpoint of your own.',
            ),
            const SizedBox(height: 8),
            const NumberedSteps([
              HintText('Enter the URL the collector listens on. Plain http is accepted.'),
              HintText(
                'If the collector requires authentication, enter a token; it is sent as '
                'Authorization: Bearer <token>. Leave it blank otherwise.',
              ),
            ]),
            const SizedBox(height: 8),
            const HintText(
              'Each event is one HTTP POST with Content-Type: application/json and a single JSON '
              'object as the body; any 2xx response is treated as delivered. A platform that wants '
              'its own header name — New Relic\'s Api-Key, say — needs a proxy in front to rename '
              'it, or a provider added to this list.',
            ),
          ],
      };
}
