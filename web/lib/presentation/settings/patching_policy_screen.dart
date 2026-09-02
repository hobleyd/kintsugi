import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/injection.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/settings.dart';
import '../../domain/usecases/settings_usecases.dart';
import 'patching_policy_bloc.dart';
import 'settings_state.dart';

/// How often agents patch, and how far a required restart can be deferred.
/// What `Pages/Settings/PatchingPolicy.cshtml` was.
class PatchingPolicySettingsScreen extends StatelessWidget {
  const PatchingPolicySettingsScreen({super.key});

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => PatchingPolicyBloc(
          getSettings: locator<GetPatchingPolicySettings>(),
          updateSettings: locator<UpdatePatchingPolicySettings>(),
        )..add(const PatchingPolicyRequested()),
        child: const _PatchingPolicyForm(),
      );
}

class _PatchingPolicyForm extends StatefulWidget {
  const _PatchingPolicyForm();

  @override
  State<_PatchingPolicyForm> createState() => _PatchingPolicyFormState();
}

class _PatchingPolicyFormState extends State<_PatchingPolicyForm> {
  final _intervalValue = TextEditingController();
  final _delayValue = TextEditingController();
  final _maxDelayCount = TextEditingController();

  PatchingTimeUnit _intervalUnit = PatchingTimeUnit.days;
  PatchingTimeUnit _delayUnit = PatchingTimeUnit.days;

  @override
  void dispose() {
    _intervalValue.dispose();
    _delayValue.dispose();
    _maxDelayCount.dispose();
    super.dispose();
  }

  /// Fills the controls from whatever the server holds.
  ///
  /// Driven by a listener rather than by the builder, because a builder that assigned to a
  /// controller would fight the operator for the cursor on every rebuild — and rebuilds happen on
  /// every keystroke.
  void _hydrate(PatchingPolicySettings settings) {
    _intervalValue.text = settings.intervalValue.toString();
    _delayValue.text = settings.delayValue.toString();
    _maxDelayCount.text = settings.maxDelayCount.toString();
    setState(() {
      _intervalUnit = settings.intervalUnit;
      _delayUnit = settings.delayUnit;
    });
  }

  void _save() {
    context.read<PatchingPolicyBloc>().add(PatchingPolicySaveRequested(PatchingPolicySettings(
          // Parsed leniently on purpose: the server validates these, and rejecting a half-typed
          // number in the client would mean two sets of rules to keep in step. A blank field falls
          // back to the default the server itself would use.
          intervalValue: int.tryParse(_intervalValue.text) ?? 7,
          intervalUnit: _intervalUnit,
          delayValue: int.tryParse(_delayValue.text) ?? 1,
          delayUnit: _delayUnit,
          maxDelayCount: int.tryParse(_maxDelayCount.text) ?? 3,
        )));
  }

  @override
  Widget build(BuildContext context) =>
      BlocConsumer<PatchingPolicyBloc, SettingsState<PatchingPolicySettings>>(
        listenWhen: (previous, current) => previous.value != current.value && current.value != null,
        listener: (context, state) => _hydrate(state.value!),
        builder: (context, state) => PageScaffold(
          title: 'Patching Policy',
          subtitle: 'How often the kintsugi-agent should check for and apply patches, and — when '
              'installing one needs an application restart or a host reboot — how long that can be '
              'deferred and how many times before it must go through regardless.',
          children: [
            if (state.error != null) AlertBox.error(state.error!),
            if (state.saved) const AlertBox.success('Patching policy saved.'),
            SettingsFormPanel(
              children: [
                LabelledField(
                  label: 'Patch every',
                  hints: const [HintText('How often the agent should check for and apply patches.')],
                  child: _ValueAndUnit(
                    controller: _intervalValue,
                    unit: _intervalUnit,
                    onUnitChanged: (unit) => setState(() => _intervalUnit = unit),
                    onEdited: () => context.read<PatchingPolicyBloc>().add(const PatchingPolicyEdited()),
                    errorText: state.errorFor('IntervalValue'),
                  ),
                ),
                LabelledField(
                  label: 'Delay length',
                  hints: const [
                    HintText(
                      'How long a single deferral lasts, when a required restart or reboot is '
                      'postponed.',
                    ),
                  ],
                  child: _ValueAndUnit(
                    controller: _delayValue,
                    unit: _delayUnit,
                    onUnitChanged: (unit) => setState(() => _delayUnit = unit),
                    onEdited: () => context.read<PatchingPolicyBloc>().add(const PatchingPolicyEdited()),
                    errorText: state.errorFor('DelayValue'),
                  ),
                ),
                LabelledField(
                  label: 'Maximum number of delays',
                  hints: const [
                    HintText(
                      'How many times a required restart or reboot can be postponed before the agent '
                      'must force it through. Zero means it can never be deferred.',
                    ),
                  ],
                  child: SizedBox(
                    width: 128,
                    child: KintsugiTextField(
                      controller: _maxDelayCount,
                      keyboardType: TextInputType.number,
                      errorText: state.errorFor('MaxDelayCount'),
                      onChanged: (_) =>
                          context.read<PatchingPolicyBloc>().add(const PatchingPolicyEdited()),
                    ),
                  ),
                ),
                Align(
                  alignment: Alignment.centerLeft,
                  child: PrimaryButton(
                    label: 'Save Policy',
                    busy: state.saving,
                    onPressed: state.loading ? null : _save,
                  ),
                ),
              ],
            ),
          ],
        ),
      );
}

/// A number beside its unit — `.value-unit-row`.
class _ValueAndUnit extends StatelessWidget {
  const _ValueAndUnit({
    required this.controller,
    required this.unit,
    required this.onUnitChanged,
    required this.onEdited,
    this.errorText,
  });

  final TextEditingController controller;
  final PatchingTimeUnit unit;
  final ValueChanged<PatchingTimeUnit> onUnitChanged;
  final VoidCallback onEdited;
  final String? errorText;

  @override
  Widget build(BuildContext context) => Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 96,
            child: KintsugiTextField(
              controller: controller,
              keyboardType: TextInputType.number,
              errorText: errorText,
              onChanged: (_) => onEdited(),
            ),
          ),
          const SizedBox(width: 10),
          Expanded(
            child: KintsugiDropdown<PatchingTimeUnit>(
              value: unit,
              items: PatchingTimeUnit.values,
              labelOf: (value) => value.label,
              onChanged: (value) {
                onUnitChanged(value);
                onEdited();
              },
            ),
          ),
        ],
      );
}
