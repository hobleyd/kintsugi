import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/locator.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/settings.dart';
import '../../domain/usecases/settings_usecases.dart';
import 'vanta_bloc.dart';

/// Pushes this fleet's patch state into Vanta as compliance evidence, through Vanta's
/// "Build integrations" resource-sync API.
///
/// What gets synced, and what deliberately does not, is stated on the screen itself rather than
/// only in the code: an administrator wiring this up is entitled to know that the severity is a
/// number they chose and not a CVSS score, and that no endpoint-hardening evidence is being sent on
/// their behalf.
class VantaSettingsScreen extends StatelessWidget {
  const VantaSettingsScreen({super.key});

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => VantaSettingsBloc(
          getSettings: locator<GetVantaSettings>(),
          updateSettings: locator<UpdateVantaSettings>(),
          getSyncStatus: locator<GetVantaSyncStatus>(),
          startSync: locator<StartVantaSync>(),
        )..add(const VantaSettingsRequested()),
        child: const _VantaForm(),
      );
}

class _VantaForm extends StatefulWidget {
  const _VantaForm();

  @override
  State<_VantaForm> createState() => _VantaFormState();
}

class _VantaFormState extends State<_VantaForm> {
  final _clientId = TextEditingController();
  final _clientSecret = TextEditingController();
  final _apiBaseUrl = TextEditingController();
  final _vulnerableComponentResourceId = TextEditingController();
  final _packageVulnerabilityResourceId = TextEditingController();
  final _consoleBaseUrl = TextEditingController();
  final _severity = TextEditingController();
  final _syncIntervalHours = TextEditingController();

  bool _enabled = false;
  bool _clearClientSecret = false;

  /// Set when a numeric box holds something that is not a number.
  ///
  /// Checked here rather than left to the server, unlike every other field on this screen, because
  /// the two numeric fields are the one place where "unparseable" and "leave it alone" would arrive
  /// as the same thing: the command reads them as nullable, and null means "keep the stored value".
  /// Typing `twelve` into Severity would otherwise report "Vanta settings saved." over a value that
  /// silently did not change.
  String? _severityError;
  String? _syncIntervalError;

  @override
  void dispose() {
    _clientId.dispose();
    _clientSecret.dispose();
    _apiBaseUrl.dispose();
    _vulnerableComponentResourceId.dispose();
    _packageVulnerabilityResourceId.dispose();
    _consoleBaseUrl.dispose();
    _severity.dispose();
    _syncIntervalHours.dispose();
    super.dispose();
  }

  void _hydrate(VantaSettings settings) {
    _clientId.text = settings.clientId;
    // The effective value, default included — the operator should see which Vanta this server is
    // actually pointed at rather than an empty box.
    _apiBaseUrl.text = settings.apiBaseUrl;
    _vulnerableComponentResourceId.text = settings.vulnerableComponentResourceId;
    _packageVulnerabilityResourceId.text = settings.packageVulnerabilityResourceId;
    _consoleBaseUrl.text = settings.consoleBaseUrl;
    _severity.text = settings.severity.toString();
    _syncIntervalHours.text = settings.syncIntervalHours.toString();

    // Never repopulate the secret field, even after a successful save: the value was never sent
    // here in the first place, and echoing a submitted one back would leave it sitting in the form.
    _clientSecret.clear();
    setState(() {
      _enabled = settings.enabled;
      _clearClientSecret = false;
      _severityError = null;
      _syncIntervalError = null;
    });
  }

  void _edited() {
    if (_severityError != null || _syncIntervalError != null) {
      setState(() {
        _severityError = null;
        _syncIntervalError = null;
      });
    }
    context.read<VantaSettingsBloc>().add(const VantaSettingsEdited());
  }

  void _save() {
    final severityText = _severity.text.trim();
    final intervalText = _syncIntervalHours.text.trim();
    final severity = severityText.isEmpty ? null : double.tryParse(severityText);
    final interval = intervalText.isEmpty ? null : int.tryParse(intervalText);

    // A blank box legitimately means "keep the stored value". Text that is not a number means the
    // operator meant something and mistyped it, and sending it as null would look like a save.
    if ((severityText.isNotEmpty && severity == null) || (intervalText.isNotEmpty && interval == null)) {
      setState(() {
        _severityError = severityText.isNotEmpty && severity == null ? 'Enter a number between 0 and 10.' : null;
        _syncIntervalError = intervalText.isNotEmpty && interval == null ? 'Enter a whole number of hours.' : null;
      });
      return;
    }

    context.read<VantaSettingsBloc>().add(VantaSettingsSaveRequested(
          enabled: _enabled,
          clientId: _clientId.text.trim(),
          clientSecret: _clientSecret.text.isEmpty ? null : _clientSecret.text,
          clearClientSecret: _clearClientSecret,
          apiBaseUrl: _apiBaseUrl.text.trim(),
          vulnerableComponentResourceId: _vulnerableComponentResourceId.text.trim(),
          packageVulnerabilityResourceId: _packageVulnerabilityResourceId.text.trim(),
          consoleBaseUrl: _consoleBaseUrl.text.trim(),
          severity: severity,
          syncIntervalHours: interval,
        ));
  }

  @override
  Widget build(BuildContext context) => BlocConsumer<VantaSettingsBloc, VantaState>(
        listenWhen: (previous, current) =>
            previous.settings.value != current.settings.value && current.settings.value != null,
        listener: (context, state) => _hydrate(state.settings.value!),
        builder: (context, state) {
          final settings = state.settings.value;

          return PageScaffold(
            title: 'Vanta',
            subtitle: 'Sends this fleet’s patch state to Vanta as compliance evidence: one '
                'VulnerableComponent per managed host, and one PackageVulnerability for every '
                'out-of-date application and pending operating-system update on it. Each sync '
                'replaces everything this server previously sent, so what Vanta holds is always the '
                'current picture.',
            children: [
              if (state.settings.error != null) AlertBox.error(state.settings.error!),
              if (state.settings.saved) const AlertBox.success('Vanta settings saved.'),
              _SyncPanel(state: state),
              const SizedBox(height: 20),
              SettingsColumns(
                form: SettingsFormPanel(
                  maxWidth: double.infinity,
                  children: [
                    const SubHeadingTight('Connection'),
                    KintsugiCheckbox(
                      label: 'Sync this fleet to Vanta',
                      value: _enabled,
                      onChanged: (value) {
                        setState(() => _enabled = value);
                        _edited();
                      },
                    ),
                    if (settings != null && settings.enabled && !settings.isConfigured)
                      const AlertBox.error(
                        'Switched on, but something a sync needs is missing — nothing is being sent.',
                      ),
                    const SizedBox(height: 16),
                    LabelledField(
                      label: 'OAuth client ID',
                      hints: const [
                        HintText(
                          'From the private "Build integrations" app in Vanta’s developer '
                          'console.',
                        ),
                      ],
                      child: KintsugiTextField(
                        controller: _clientId,
                        errorText: state.settings.errorFor('ClientId'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                    LabelledField(
                      label: 'OAuth client secret',
                      hints: const [
                        HintText(
                          'Generated beside the client ID. Vanta shows it once. It is never sent '
                          'back to this screen.',
                        ),
                      ],
                      child: KintsugiTextField(
                        controller: _clientSecret,
                        obscureText: true,
                        hintText: settings?.hasClientSecret == true
                            ? 'Stored — leave blank to keep it'
                            : 'Required',
                        errorText: state.settings.errorFor('ClientSecret'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                    if (settings?.hasClientSecret == true)
                      KintsugiCheckbox(
                        label: 'Remove the stored secret',
                        value: _clearClientSecret,
                        onChanged: (value) {
                          setState(() => _clearClientSecret = value);
                          _edited();
                        },
                      ),
                    LabelledField(
                      label: 'Vanta API address',
                      hints: [
                        HintText(
                          'Leave blank for the commercial host. FedRAMP tenants use '
                          'https://api.vanta-gov.com.'
                          '${settings?.isApiBaseUrlDefault == true ? ' Currently using the default.' : ''}',
                        ),
                      ],
                      child: KintsugiTextField(
                        controller: _apiBaseUrl,
                        hintText: 'https://api.vanta.com',
                        errorText: state.settings.errorFor('ApiBaseUrl'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                    const SubHeadingTight('Registered resources'),
                    LabelledField(
                      label: 'VulnerableComponent resource ID',
                      hints: const [
                        HintText(
                          'From the app’s Resources tab. One host becomes one of these. Vanta '
                          'rejects a sync naming an ID it did not issue, so there is no default.',
                        ),
                      ],
                      child: KintsugiTextField(
                        controller: _vulnerableComponentResourceId,
                        errorText: state.settings.errorFor('VulnerableComponentResourceId'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                    LabelledField(
                      label: 'PackageVulnerabilityConnectors resource ID',
                      hints: const [
                        HintText(
                          'A separate registration in Vanta, and a separate ID — one '
                          'out-of-date application on one host becomes one of these.',
                        ),
                      ],
                      child: KintsugiTextField(
                        controller: _packageVulnerabilityResourceId,
                        errorText: state.settings.errorFor('PackageVulnerabilityResourceId'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                    const SubHeadingTight('This server'),
                    LabelledField(
                      label: 'Address of this admin UI',
                      hints: const [
                        HintText(
                          'Every synced record links back here, and Vanta requires https. This is '
                          'the address an administrator opens this screen on — not the address '
                          'agents check in on, which in a split deployment is a different hostname '
                          'entirely. It cannot be worked out from the request, because the sync '
                          'usually runs on a timer with nobody watching.',
                        ),
                      ],
                      child: KintsugiTextField(
                        controller: _consoleBaseUrl,
                        hintText: 'https://kintsugi.example.com',
                        errorText: state.settings.errorFor('ConsoleBaseUrl'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                    LabelledField(
                      label: 'Reported severity (0–10)',
                      hints: const [
                        HintText(
                          'Applied to every record. Set it to match how your Vanta vulnerability '
                          'SLAs are banded — see the note beside this form for why it is a '
                          'number you choose rather than one this server measures.',
                        ),
                      ],
                      child: KintsugiTextField(
                        controller: _severity,
                        hintText: '5.0',
                        errorText: _severityError ?? state.settings.errorFor('Severity'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                    LabelledField(
                      label: 'Sync every (hours)',
                      hints: const [
                        HintText(
                          'Each run sends the complete current picture, so this is a freshness dial '
                          '— nothing accumulates between runs.',
                        ),
                      ],
                      child: KintsugiTextField(
                        controller: _syncIntervalHours,
                        hintText: '24',
                        errorText: _syncIntervalError ?? state.settings.errorFor('SyncIntervalHours'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                  ],
                ),
                aside: const SettingsAside(
                  title: 'What is sent',
                  children: [
                    HintText(
                      'One VulnerableComponent per host that has checked in, stamped with when it '
                      'last did — not with when the sync ran. A host that has never checked in '
                      'is left out entirely rather than being reported as freshly scanned.',
                    ),
                    SizedBox(height: 12),
                    HintText(
                      'One PackageVulnerability per out-of-date application, plus one per host with '
                      'a pending operating-system update. Each says whether Kintsugi can actually '
                      'apply it — which for an application means a reviewed, signed script, the '
                      'same test the agent itself makes before running anything.',
                    ),
                    SizedBox(height: 12),
                    AlertBox.info(
                      'The severity is the number set here, applied uniformly. Kintsugi compares '
                      'installed versions against latest known versions; it has no CVE feed, so no '
                      'CVE ID, CVSS score or CVSS vector is ever sent — those fields are left '
                      'off rather than filled with a guess.',
                    ),
                    SizedBox(height: 12),
                    HintText(
                      'No endpoint-hardening evidence is sent. Vanta’s macOS and Windows '
                      'computer resources require disk encryption, screenlock policy, browser '
                      'extension and local user data that this system does not collect, and there '
                      'is no Linux equivalent at all. Reporting those from empty values would put '
                      'invented evidence behind real controls, so they are deliberately not synced.',
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 24),
              Align(
                alignment: Alignment.centerLeft,
                child: PrimaryButton(
                  label: 'Save',
                  busy: state.settings.saving,
                  onPressed: state.settings.loading ? null : _save,
                ),
              ),
            ],
          );
        },
      );
}

/// The background sync's own status, and the button that starts one now.
class _SyncPanel extends StatelessWidget {
  const _SyncPanel({required this.state});

  final VantaState state;

  @override
  Widget build(BuildContext context) {
    final sync = state.sync;
    final configured = state.settings.value?.isConfigured ?? false;

    return SettingsFormPanel(
      maxWidth: double.infinity,
      children: [
        const SubHeadingTight('Sync'),
        if (state.syncError != null) AlertBox.error(state.syncError!),
        if (sync.running)
          const HintText('A sync is running…')
        else if (sync.lastRunSucceeded == null)
          // In-memory status, so a restart resets it. Said plainly, because "no runs recorded" and
          // "the last run failed" are very different things to be looking at.
          const HintText('No sync has run since this server started.')
        else if (sync.lastRunSucceeded == true)
          HintText(
            'Last sync sent ${sync.componentCount} host(s) and ${sync.packageCount} '
            'outstanding update(s).',
          )
        else
          AlertBox.error(sync.message ?? 'The last sync did not complete.'),
        if (!sync.running && sync.lastRunSucceeded == true && sync.message != null)
          HintText(sync.message!),
        if (sync.completedUtc != null) ...[
          const SizedBox(height: 6),
          Row(
            children: [
              const HintText('Finished '),
              LocalTimestamp(sync.completedUtc),
            ],
          ),
        ],
        const SizedBox(height: 12),
        Align(
          alignment: Alignment.centerLeft,
          child: SecondaryButton(
            label: 'Sync now',
            tooltip: configured
                ? null
                : 'Finish configuring the integration first — a sync would send nothing.',
            onPressed: sync.running || !configured
                ? null
                : () => context.read<VantaSettingsBloc>().add(const VantaSyncRequested()),
          ),
        ),
      ],
    );
  }
}
