import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/locator.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/vulnerability.dart';
import '../../domain/usecases/vulnerability_usecases.dart';
import 'vulnerabilities_bloc.dart';

/// Configures the assessment of this fleet's installed versions against published CVE data.
///
/// The limits are on the screen rather than only in the code, the way the Vanta screen states
/// what it does not send. An administrator switching this on is entitled to know that the API key
/// only buys speed, that nothing is assessed until somebody has confirmed a CPE, and that the AI
/// is only ever asked for a lookup key which NVD then has to confirm exists.
class VulnerabilitiesSettingsScreen extends StatelessWidget {
  const VulnerabilitiesSettingsScreen({super.key});

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => VulnerabilitySettingsBloc(
          getSettings: locator<GetVulnerabilitySettings>(),
          updateSettings: locator<UpdateVulnerabilitySettings>(),
          getRunStatus: locator<GetVulnerabilityRunStatus>(),
          startRun: locator<StartVulnerabilityRun>(),
          cancelRun: locator<CancelVulnerabilityRun>(),
        )..add(const VulnerabilitySettingsRequested()),
        child: const _VulnerabilitiesForm(),
      );
}

class _VulnerabilitiesForm extends StatefulWidget {
  const _VulnerabilitiesForm();

  @override
  State<_VulnerabilitiesForm> createState() => _VulnerabilitiesFormState();
}

class _VulnerabilitiesFormState extends State<_VulnerabilitiesForm> {
  final _nvdApiKey = TextEditingController();
  final _syncIntervalHours = TextEditingController();
  final _assessmentsPerRun = TextEditingController();
  final _packagesPerRun = TextEditingController();

  bool _enabled = false;
  bool _clearNvdApiKey = false;
  bool _autoSuggestCpes = true;

  /// Set when a numeric box holds something that is not a number. Checked here rather than left to
  /// the server for the reason the Vanta screen gives: the command reads these as nullable and
  /// null means "keep the stored value", so `twelve` would report a successful save over a value
  /// that silently did not change.
  String? _syncIntervalError;
  String? _assessmentsPerRunError;
  String? _packagesPerRunError;

  @override
  void dispose() {
    _nvdApiKey.dispose();
    _syncIntervalHours.dispose();
    _assessmentsPerRun.dispose();
    _packagesPerRun.dispose();
    super.dispose();
  }

  void _hydrate(VulnerabilitySettings settings) {
    _syncIntervalHours.text = settings.syncIntervalHours.toString();
    _assessmentsPerRun.text = settings.assessmentsPerRun.toString();
    _packagesPerRun.text = settings.packagesPerRun.toString();

    // Never repopulate the key field, even after a successful save: the value was never sent here
    // in the first place, and echoing a submitted one back would leave it sitting in the form.
    _nvdApiKey.clear();
    setState(() {
      _enabled = settings.enabled;
      _autoSuggestCpes = settings.autoSuggestCpes;
      _clearNvdApiKey = false;
      _syncIntervalError = null;
      _assessmentsPerRunError = null;
      _packagesPerRunError = null;
    });
  }

  void _edited() {
    if (_syncIntervalError != null || _assessmentsPerRunError != null || _packagesPerRunError != null) {
      setState(() {
        _syncIntervalError = null;
        _assessmentsPerRunError = null;
        _packagesPerRunError = null;
      });
    }
    context.read<VulnerabilitySettingsBloc>().add(const VulnerabilitySettingsEdited());
  }

  void _save() {
    final intervalText = _syncIntervalHours.text.trim();
    final perRunText = _assessmentsPerRun.text.trim();
    final interval = intervalText.isEmpty ? null : int.tryParse(intervalText);
    final perRun = perRunText.isEmpty ? null : int.tryParse(perRunText);
    final packagesText = _packagesPerRun.text.trim();
    final packages = packagesText.isEmpty ? null : int.tryParse(packagesText);

    if ((intervalText.isNotEmpty && interval == null)
        || (perRunText.isNotEmpty && perRun == null)
        || (packagesText.isNotEmpty && packages == null)) {
      setState(() {
        _syncIntervalError =
            intervalText.isNotEmpty && interval == null ? 'Enter a whole number of hours.' : null;
        _assessmentsPerRunError =
            perRunText.isNotEmpty && perRun == null ? 'Enter a whole number.' : null;
        _packagesPerRunError =
            packagesText.isNotEmpty && packages == null ? 'Enter a whole number.' : null;
      });
      return;
    }

    context.read<VulnerabilitySettingsBloc>().add(VulnerabilitySettingsSaveRequested(
          enabled: _enabled,
          nvdApiKey: _nvdApiKey.text.isEmpty ? null : _nvdApiKey.text,
          clearNvdApiKey: _clearNvdApiKey,
          syncIntervalHours: interval,
          assessmentsPerRun: perRun,
          packagesPerRun: packages,
          autoSuggestCpes: _autoSuggestCpes,
        ));
  }

  @override
  Widget build(BuildContext context) =>
      BlocConsumer<VulnerabilitySettingsBloc, VulnerabilitySettingsState>(
        listenWhen: (previous, current) =>
            previous.settings.value != current.settings.value && current.settings.value != null,
        listener: (context, state) => _hydrate(state.settings.value!),
        builder: (context, state) {
          final settings = state.settings.value;

          return PageScaffold(
            title: 'Vulnerabilities',
            subtitle: 'Matches the versions this fleet has installed against published CVEs from '
                'the National Vulnerability Database, and flags the ones CISA lists as actively '
                'exploited. Nothing is assessed until somebody has confirmed which CPE an '
                'application is — see the Vulnerabilities screen’s mapping queue.',
            children: [
              if (state.settings.error != null) AlertBox.error(state.settings.error!),
              if (state.settings.saved) const AlertBox.success('Vulnerability settings saved.'),
              _RunPanel(state: state),
              const SizedBox(height: 20),
              SettingsColumns(
                form: SettingsFormPanel(
                  maxWidth: double.infinity,
                  children: [
                    const SubHeadingTight('Assessment'),
                    KintsugiCheckbox(
                      label: 'Assess this fleet against published CVEs',
                      value: _enabled,
                      onChanged: (value) {
                        setState(() => _enabled = value);
                        _edited();
                      },
                    ),
                    const SizedBox(height: 16),
                    LabelledField(
                      label: 'NVD API key',
                      hints: const [
                        HintText(
                          'Optional, free, and the only thing it changes is speed: NVD allows 5 '
                          'requests per 30 seconds without one and 50 with. For a few hundred '
                          'installed versions that is minutes against roughly an hour and a half. '
                          'Request one at nvd.nist.gov/developers/request-an-api-key. It is never '
                          'sent back to this screen.',
                        ),
                      ],
                      child: KintsugiTextField(
                        controller: _nvdApiKey,
                        obscureText: true,
                        hintText: settings?.hasNvdApiKey == true
                            ? 'Stored — leave blank to keep it'
                            : 'Optional',
                        errorText: state.settings.errorFor('NvdApiKey'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                    if (settings?.hasNvdApiKey == true)
                      KintsugiCheckbox(
                        label: 'Remove the stored key',
                        value: _clearNvdApiKey,
                        onChanged: (value) {
                          setState(() => _clearNvdApiKey = value);
                          _edited();
                        },
                      ),
                    LabelledField(
                      label: 'Assess every (hours)',
                      hints: const [
                        HintText(
                          'How often a run starts. Each run refreshes the exploited-CVE catalogue '
                          'and then re-checks the least recently assessed versions, so this is a '
                          'freshness dial.',
                        ),
                      ],
                      child: KintsugiTextField(
                        controller: _syncIntervalHours,
                        hintText: '24',
                        errorText: _syncIntervalError ?? state.settings.errorFor('SyncIntervalHours'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                    LabelledField(
                      label: 'Versions checked per run',
                      hints: const [
                        HintText(
                          'A run is deliberately partial. Versions are taken least-recently-checked '
                          'first and each answer is saved as it arrives, so the queue drains across '
                          'runs and a restart costs one query rather than all of them. Between 10 '
                          'and 5000.',
                        ),
                      ],
                      child: KintsugiTextField(
                        controller: _assessmentsPerRun,
                        hintText: '250',
                        errorText:
                            _assessmentsPerRunError ?? state.settings.errorFor('AssessmentsPerRun'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                    LabelledField(
                      label: 'Linux packages checked per run',
                      hints: const [
                        HintText(
                          'Its own figure, an order of magnitude larger than the one above, '
                          'because the two are limited by different things: NVD allows a handful '
                          'of requests a minute and takes one version at a time, while the '
                          'database covering Linux packages takes two hundred per call and '
                          'rate-limits nothing. Between 10 and 50000.',
                        ),
                      ],
                      child: KintsugiTextField(
                        controller: _packagesPerRun,
                        hintText: '4000',
                        errorText: _packagesPerRunError ?? state.settings.errorFor('PackagesPerRun'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                    const SubHeadingTight('CPE mapping'),
                    KintsugiCheckbox(
                      label: 'Let the AI agent propose a CPE for applications it recognises',
                      value: _autoSuggestCpes,
                      onChanged: (value) {
                        setState(() => _autoSuggestCpes = value);
                        _edited();
                      },
                    ),
                    const HintText(
                      'Uses whichever provider is configured on the AI Agent settings page. A '
                      'proposal is discarded unless NVD’s own dictionary contains that vendor and '
                      'product, and somebody still has to accept it before anything is assessed.',
                    ),
                  ],
                ),
                aside: const SettingsAside(
                  title: 'What this does and does not know',
                  children: [
                    HintText(
                      'NVD decides which versions a CVE affects. Kintsugi sends it a CPE name '
                      'carrying the installed version and stores what comes back — it does not '
                      'interpret version ranges itself, so there is no second opinion to drift.',
                    ),
                    SizedBox(height: 12),
                    AlertBox.info(
                      'Nothing is assessed for an application until somebody confirms which CPE it '
                      'is. That is not a formality: NVD’s dictionary ranks Slackware Linux first '
                      'for “slack” and ZoomText for “zoom”, and a wrong mapping attributes another '
                      'product’s vulnerabilities to yours. Unmapped applications are counted on the '
                      'Vulnerabilities screen rather than quietly left out.',
                    ),
                    SizedBox(height: 12),
                    HintText(
                      'The AI is asked for one thing only — the vendor and product tokens NVD '
                      'indexes a product under. It is never asked whether anything is vulnerable, '
                      'which CVEs apply, or how serious they are; those come from NVD, and a '
                      'model’s answer would be a plausible-looking second opinion nothing checks.',
                    ),
                    SizedBox(height: 12),
                    HintText(
                      'Operating systems are assessed too, but coverage differs. macOS reports '
                      'everything needed. Windows needs the build’s update revision and Linux '
                      'needs its os-release identifier and version, both of which only newer '
                      'agents send — hosts running an older agent are counted as not assessed and '
                      'say so, rather than being guessed at.',
                    ),
                    SizedBox(height: 12),
                    HintText(
                      'Linux hosts are assessed twice over. The release itself goes to NVD, which '
                      'holds little about a distribution; the packages apt or dnf installed go to '
                      'a database of the distributions’ own advisories, which is the one that '
                      'matters — and which accounts for backported fixes, so a version your '
                      'distribution has already patched is not reported as vulnerable. Packages '
                      'need no CPE confirmation: a distribution’s own package name is '
                      'unambiguous.',
                    ),
                    SizedBox(height: 12),
                    HintText(
                      'Those packages are recorded for assessment only. They never appear on the '
                      'Applications screen, are never given an upgrade script, and are never '
                      'patched by this system — apt and dnf remain outside what Kintsugi updates.',
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

/// The background run's own status, and the button that starts one now.
class _RunPanel extends StatelessWidget {
  const _RunPanel({required this.state});

  final VulnerabilitySettingsState state;

  @override
  Widget build(BuildContext context) {
    final run = state.run;
    final enabled = state.settings.value?.enabled ?? false;

    return SettingsFormPanel(
      maxWidth: double.infinity,
      children: [
        const SubHeadingTight('Assessment run'),
        if (state.runError != null) AlertBox.error(state.runError!),
        if (run.running)
          _RunProgress(run: run)
        // Cancelled is read before the two below it, and has to be: a cancelled run reports
        // `lastRunSucceeded` as null, the same as a server that has run nothing — and it is an
        // info box rather than an error, because every stage commits as it goes, so stopping one
        // kept real work. Red here would say the opposite of what happened.
        else if (run.lastRunCancelled)
          AlertBox.info(run.message ?? 'The last assessment was stopped.')
        else if (run.lastRunSucceeded == null)
          // In-memory status, so a restart resets it. Said plainly, because "no runs recorded" and
          // "the last run failed" are very different things to be looking at.
          const HintText('No assessment has run since this server started.')
        else if (run.lastRunSucceeded == true)
          HintText('Last run checked ${run.assessmentsCompleted} installed version(s).')
        else
          AlertBox.error(run.message ?? 'The last assessment did not complete.'),
        if (run.assessmentsRemaining > 0) ...[
          const SizedBox(height: 6),
          // Not an error, and worth saying so: a run is bounded on purpose, so a queue left over
          // is the design working rather than a backlog.
          HintText(
            '${run.assessmentsRemaining} installed version(s) still to check. A run stops at the '
            'per-run limit and the next one continues from there.',
          ),
        ],
        if (run.completedUtc != null) ...[
          const SizedBox(height: 6),
          Row(
            children: [
              const HintText('Finished '),
              LocalTimestamp(run.completedUtc),
            ],
          ),
        ],
        const SizedBox(height: 12),
        Align(
          alignment: Alignment.centerLeft,
          // One button in two states rather than two buttons, because at any moment exactly one of
          // them is a thing that can be done — and a permanently-greyed Cancel beside Assess now
          // would be a second dead control on a panel whose whole job is saying what is happening.
          child: run.running
              ? SecondaryButton(
                  label: run.cancelling ? 'Cancelling…' : 'Cancel assessment',
                  tooltip: run.cancelling
                      ? 'Already stopping. The run is inside a request to NVD or OSV and stops '
                          'when it answers.'
                      : 'Stops the run. Everything already checked is saved, and the next run '
                          'continues from there.',
                  // Disabled once asked rather than left live: a second press is a 409, and the
                  // honest answer to "I already pressed it" is that it is stopping, not an error.
                  onPressed: run.cancelling
                      ? null
                      : () => context
                          .read<VulnerabilitySettingsBloc>()
                          .add(const VulnerabilityRunCancelRequested()),
                )
              : SecondaryButton(
                  label: 'Assess now',
                  tooltip: enabled ? null : 'Switch the assessment on first.',
                  onPressed: enabled
                      ? () => context
                          .read<VulnerabilitySettingsBloc>()
                          .add(const VulnerabilityRunRequested())
                      : null,
                ),
        ),
      ],
    );
  }
}

/// What the run is doing right now: a bar, the stage, and the one thing being checked.
///
/// The subject is the reason this exists. A run is minutes to hours — bounded by NVD's five
/// requests per thirty seconds rather than by how fast this server works — so "an assessment is
/// running…", which is what stood here, is indistinguishable from a run that has hung. A moving
/// package name is the difference.
///
/// **The bar measures the current stage, never the whole run.** The five stages cost wildly
/// different amounts, so one bar across all of them would sit between 4% and 6% for most of a run
/// and then leap; the stage names itself instead. A stage with nothing to count — the KEV
/// download, the discovery pass — reports no total and gets an indeterminate bar, which is honest
/// where "0 of 0" would not be.
class _RunProgress extends StatelessWidget {
  const _RunProgress({required this.run});

  final VulnerabilityRunStatus run;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          run.stage ?? 'Starting the assessment…',
          style: Theme.of(context).textTheme.bodyMedium,
        ),
        const SizedBox(height: 8),
        ClipRRect(
          borderRadius: BorderRadius.circular(3),
          child: LinearProgressIndicator(
            // Null is the indeterminate animation, which is exactly right for a stage that cannot
            // count itself — see VulnerabilityRunStatus.fraction.
            value: run.fraction,
            minHeight: 6,
            backgroundColor: palette.border,
            color: palette.neon,
          ),
        ),
        const SizedBox(height: 6),
        Row(
          children: [
            if (run.subject != null)
              // Ellipsised rather than wrapped: a long package version must not make this panel
              // grow and shrink as the name under the bar changes every few seconds.
              Expanded(
                child: Text(
                  run.subject!,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(color: palette.muted),
                ),
              )
            else
              const Spacer(),
            if (run.stepsTotal > 0) ...[
              const SizedBox(width: 12),
              HintText('${run.stepsCompleted} of ${run.stepsTotal}'),
            ],
          ],
        ),
        if (run.cancelling) ...[
          const SizedBox(height: 6),
          const HintText(
            'Stopping. The run finishes the request it is in and keeps everything already checked.',
          ),
        ],
      ],
    );
  }
}
