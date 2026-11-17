import 'package:flutter/material.dart';

import '../../core/di/locator.dart';
import '../../core/network/api_exception.dart';
import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/ai_usage.dart';
import '../../domain/usecases/ai_usage_usecases.dart';

/// What the AI has spent this month against the limit, and the limit itself.
///
/// Amounts can be read in US dollars, Australian dollars or tokens, switched with one click and
/// kept by this screen alone: the server only speaks dollars and tokens. Costs are **estimates**
/// (the tokens a provider reported times the models.dev price list), and the screen says so.
class AiUsageScreen extends StatefulWidget {
  const AiUsageScreen({super.key});

  @override
  State<AiUsageScreen> createState() => _AiUsageScreenState();
}

class _AiUsageScreenState extends State<AiUsageScreen> {
  final _budget = TextEditingController();
  final _rate = TextEditingController();

  AiUsage? _usage;
  String? _error;
  String? _notice;
  bool _saving = false;
  AiUsageUnit _unit = AiUsageUnit.usd;
  String _currency = 'USD';
  int _warnPercent = 80;
  bool _stop = true;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _budget.dispose();
    _rate.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final usage = await locator<GetAiUsage>()();
      if (!mounted) return;
      setState(() {
        _usage = usage;
        _error = null;
        final s = usage.settings;
        _budget.text = s.budgetAmount == null ? '' : _plain(s.budgetAmount!);
        _rate.text = _plain(s.usdToAudRate);
        _currency = s.budgetCurrency;
        _warnPercent = s.warnPercent;
        _stop = s.stopAtLimit;
      });
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    }
  }

  static String _plain(double value) =>
      value == value.roundToDouble() ? value.toStringAsFixed(0) : value.toString();

  double get _rateValue => double.tryParse(_rate.text.trim()) ?? 1.55;

  Future<void> _save() async {
    final amountText = _budget.text.trim();
    final amount = amountText.isEmpty ? null : double.tryParse(amountText);
    if (amountText.isNotEmpty && (amount == null || amount <= 0)) {
      setState(() => _error = 'The monthly budget must be a number above zero, or empty for no limit.');
      return;
    }
    // The server stores the budget to the cent and refuses anything finer rather than rounding it
    // (see AiBudgetSettings.BudgetDecimalPlaces): 0.004 used to be stored as a zero limit.
    if (amount != null && (amount < 0.01 || (amount * 100).roundToDouble() / 100 != amount)) {
      setState(() => _error = 'The monthly budget must be at least 0.01 with no more than two decimal places, or empty for no limit.');
      return;
    }
    final rate = double.tryParse(_rate.text.trim());
    if (rate == null || rate <= 0) {
      setState(() => _error = 'The exchange rate must be a number above zero.');
      return;
    }

    setState(() {
      _saving = true;
      _error = null;
      _notice = null;
    });
    try {
      await locator<SaveAiBudget>()(AiBudgetSettings(
        budgetAmount: amount,
        budgetCurrency: _currency,
        usdToAudRate: rate,
        warnPercent: _warnPercent,
        stopAtLimit: _stop,
      ));
      await _load();
      if (mounted) setState(() => _notice = 'Saved.');
    } on ApiException catch (error) {
      if (mounted) {
        setState(() => _error = error.validationErrors.isEmpty
            ? error.message
            : error.validationErrors.values.expand((m) => m).join(' '));
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final usage = _usage;
    final format = AiUsageFormat(_rateValue);

    return PageScaffold(
      title: 'AI Usage',
      children: [
        const SectionHeader(
          title: 'What The AI Spends',
          hints: [
            HintText(
              'What the AI research has spent this month, and a limit that stops new AI calls so a '
              'runaway scan cannot run up a bill.',
            ),
            HintText(
              'Costs are estimates: the tokens each provider reported, times the per-million-token '
              'prices from models.dev. The provider\'s own invoice is the truth. Ollama is local and '
              'free; connections that run on a subscription (Claude Agent SDK, Goose) do not report '
              'tokens, so they never appear here.',
            ),
          ],
        ),
        if (_error != null) ...[AlertBox.error(_error!), const SizedBox(height: 12)],
        if (_notice != null) ...[AlertBox.success(_notice!), const SizedBox(height: 12)],
        if (usage == null)
          EmptyPanel(_error == null ? 'Loading…' : 'Could not load the AI usage.')
        else ...[
          _UnitSwitch(unit: _unit, onChanged: (unit) => setState(() => _unit = unit), format: format),
          const SizedBox(height: 16),
          _MonthPanel(usage: usage, unit: _unit, format: format, budgetCurrency: usage.settings.budgetCurrency),
          const SizedBox(height: 16),
          Wrap(
            spacing: 16,
            runSpacing: 16,
            crossAxisAlignment: WrapCrossAlignment.start,
            children: [
              SizedBox(width: 420, child: _limitPanel(context)),
              SizedBox(width: 560, child: _DaysPanel(usage: usage, unit: _unit, format: format)),
            ],
          ),
          const SizedBox(height: 16),
          _BreakdownPanel(usage: usage, unit: _unit, format: format),
        ],
      ],
    );
  }

  Widget _limitPanel(BuildContext context) => KintsugiPanel(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('Spending limit', style: Theme.of(context).textTheme.titleMedium),
              const SizedBox(height: 10),
              LabelledField(
                label: 'Monthly budget',
                hints: const [HintText('Leave empty for no limit.')],
                child: Row(
                  children: [
                    Expanded(
                      child: KintsugiTextField(
                        controller: _budget,
                        hintText: 'e.g. 50',
                        keyboardType: TextInputType.number,
                        enabled: !_saving,
                      ),
                    ),
                    const SizedBox(width: 8),
                    SizedBox(
                      width: 100,
                      child: KintsugiDropdown<String>(
                        value: _currency,
                        items: const ['USD', 'AUD'],
                        onChanged: _saving ? null : (value) => setState(() => _currency = value),
                      ),
                    ),
                  ],
                ),
              ),
              LabelledField(
                label: 'Exchange rate (1 USD = ? AUD)',
                hints: const [
                  HintText(
                    'Set by you. Not fetched live, so a limit never moves because an outside rate '
                    'service did, or was down.',
                  ),
                ],
                child: SizedBox(
                  width: 140,
                  child: KintsugiTextField(
                    controller: _rate,
                    keyboardType: TextInputType.number,
                    enabled: !_saving,
                    onChanged: (_) => setState(() {}),
                  ),
                ),
              ),
              LabelledField(
                label: 'Warn at',
                child: SizedBox(
                  width: 140,
                  child: KintsugiDropdown<int>(
                    value: _warnPercent,
                    items: const [60, 70, 80, 90],
                    labelOf: (value) => '$value%',
                    onChanged: _saving ? null : (value) => setState(() => _warnPercent = value),
                  ),
                ),
              ),
              KintsugiCheckbox(
                label: 'Stop new AI calls at 100%',
                value: _stop,
                onChanged: (value) => setState(() => _stop = value),
              ),
              const SizedBox(height: 6),
              const HintText(
                'At the limit, research, script repair and CPE suggestions are refused with a clear '
                'message until the month turns or the budget is raised. Patching is not affected: '
                'agents only run scripts that are already signed, and nothing in a patch cycle calls '
                'the AI.',
              ),
              const SizedBox(height: 14),
              PrimaryButton(label: 'Save', busy: _saving, onPressed: _save),
            ],
          ),
        ),
      );
}

class _UnitSwitch extends StatelessWidget {
  const _UnitSwitch({required this.unit, required this.onChanged, required this.format});

  final AiUsageUnit unit;
  final void Function(AiUsageUnit unit) onChanged;
  final AiUsageFormat format;

  @override
  Widget build(BuildContext context) => Wrap(
        spacing: 12,
        runSpacing: 8,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          const HintText('Show amounts as'),
          for (final option in AiUsageUnit.values)
            unit == option
                ? PrimaryButton(label: option.label, onPressed: () => onChanged(option))
                : SecondaryButton(label: option.label, onPressed: () => onChanged(option)),
          HintText(switch (unit) {
            AiUsageUnit.tokens => 'Tokens are exact: what the provider reports. The limit is still a money amount.',
            AiUsageUnit.aud => 'Converted from US dollars at your exchange rate.',
            AiUsageUnit.usd => 'Prices are in US dollars, as models.dev lists them.',
          }),
        ],
      );
}

class _MonthPanel extends StatelessWidget {
  const _MonthPanel({required this.usage, required this.unit, required this.format, required this.budgetCurrency});

  final AiUsage usage;
  final AiUsageUnit unit;
  final AiUsageFormat format;
  final String budgetCurrency;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final status = usage.status;
    final budget = status.budgetUsd;
    final fraction = budget == null ? 0.0 : (status.spentUsd / budget).clamp(0.0, 1.0);
    final colour = status.limitReached
        ? palette.red
        : status.warnReached
            ? palette.amber
            : palette.green;
    // In tokens the headline is the tokens, and the money it came to stays beside it: the limit is
    // a money amount whatever is being looked at.
    final money = unit == AiUsageUnit.aud ? AiUsageUnit.aud : AiUsageUnit.usd;
    final headline = unit == AiUsageUnit.tokens
        ? '${AiUsageFormat.tokens(usage.totalTokens)} tokens'
        : format.amount(unit, costUsd: status.spentUsd, tokens: usage.totalTokens);
    final of = budget == null
        ? 'No limit is set.'
        : unit == AiUsageUnit.tokens
            ? '(about ${format.amount(money, costUsd: status.spentUsd, tokens: 0)} of a '
                '${format.amount(money, costUsd: budget, tokens: 0)} budget, ${status.percent}%)'
            : 'of ${format.amount(unit, costUsd: budget, tokens: 0)} budget (${status.percent}%)';

    return KintsugiPanel(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (status.blocked)
              Padding(
                padding: const EdgeInsets.only(bottom: 12),
                child: AlertBox.error(
                  'The limit is reached. New AI calls are being refused until '
                  '${_monthName(usage.nextMonthStartUtc)} or until the budget is raised.',
                ),
              )
            else if (status.limitReached)
              const Padding(
                padding: EdgeInsets.only(bottom: 12),
                child: AlertBox.info(
                  'The limit is reached, but the stop is off, so AI calls are still being made and billed.',
                ),
              )
            else if (status.warnReached)
              Padding(
                padding: const EdgeInsets.only(bottom: 12),
                child: AlertBox.info(
                  '${status.percent}% of the monthly budget is spent. At 100% new AI calls will be refused.',
                ),
              ),
            Wrap(
              crossAxisAlignment: WrapCrossAlignment.end,
              spacing: 14,
              children: [
                Text(headline, style: AppTheme.display(color: palette.text, size: 28)),
                HintText(of),
                HintText('Resets on the 1st (${_monthName(usage.nextMonthStartUtc)})'),
              ],
            ),
            const SizedBox(height: 10),
            ClipRRect(
              borderRadius: BorderRadius.circular(3),
              child: LinearProgressIndicator(
                value: fraction,
                minHeight: 14,
                backgroundColor: palette.backgroundAlt,
                valueColor: AlwaysStoppedAnimation(colour),
              ),
            ),
            const SizedBox(height: 6),
            HintText(
              budget == null
                  ? 'Set a monthly budget below to get a warning and a stop.'
                  : status.limitReached
                      ? 'The limit has been reached.'
                      : 'About ${format.amount(money, costUsd: budget - status.spentUsd, tokens: 0)} left this month.',
            ),
            if (status.unpricedCalls > 0) ...[
              const SizedBox(height: 4),
              HintText(
                'Includes about ${format.amount(money, costUsd: status.unpricedEstimateUsd, tokens: 0)} for '
                '${status.unpricedCalls} ${status.unpricedCalls == 1 ? 'call' : 'calls'} to models with no catalogue '
                'price, counted at a high fallback rate so they still count toward the limit.',
              ),
            ],
          ],
        ),
      ),
    );
  }

  static String _monthName(DateTime utc) {
    const months = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
    return '${months[utc.month - 1]} ${utc.day}';
  }
}

class _DaysPanel extends StatelessWidget {
  const _DaysPanel({required this.usage, required this.unit, required this.format});

  final AiUsage usage;
  final AiUsageUnit unit;
  final AiUsageFormat format;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    double value(AiUsageDay day) => unit == AiUsageUnit.tokens ? day.tokens.toDouble() : day.costUsd;
    final peak = usage.days.map(value).fold<double>(0, (a, b) => a > b ? a : b);
    final average = usage.days.isEmpty ? 0.0 : usage.days.map(value).reduce((a, b) => a + b) / usage.days.length;

    return KintsugiPanel(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('Last 30 days', style: Theme.of(context).textTheme.titleMedium),
            HintText(unit == AiUsageUnit.tokens ? 'Tokens per day' : 'Estimated cost per day (${unit.label})'),
            const SizedBox(height: 12),
            SizedBox(
              height: 110,
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  for (final day in usage.days)
                    Expanded(
                      child: Tooltip(
                        message: '${day.day.year}-${day.day.month.toString().padLeft(2, '0')}-${day.day.day.toString().padLeft(2, '0')}: '
                            '${format.amount(unit, costUsd: day.costUsd, tokens: day.tokens)}',
                        child: Container(
                          margin: const EdgeInsets.symmetric(horizontal: 1.5),
                          height: peak == 0 ? 2 : (value(day) * 100 / peak).clamp(2.0, 100.0),
                          // A day well above the month's usual is amber: the runaway day is the one
                          // this chart is for.
                          color: average > 0 && value(day) > average * 4 ? palette.amber : palette.neon.withValues(alpha: 0.75),
                        ),
                      ),
                    ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _BreakdownPanel extends StatelessWidget {
  const _BreakdownPanel({required this.usage, required this.unit, required this.format});

  final AiUsage usage;
  final AiUsageUnit unit;
  final AiUsageFormat format;

  @override
  Widget build(BuildContext context) {
    final style = Theme.of(context).textTheme.titleMedium;

    Widget row(String left, String right, {String? hint}) => Padding(
          padding: const EdgeInsets.symmetric(vertical: 3),
          child: Row(
            children: [
              Expanded(child: Text(left)),
              if (hint != null) HintText(hint),
              const SizedBox(width: 14),
              Text(right),
            ],
          ),
        );

    return KintsugiPanel(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('By feature', style: style),
            const SizedBox(height: 6),
            if (usage.byFeature.isEmpty) const HintText('Nothing has been recorded this month.'),
            for (final f in usage.byFeature)
              row(
                f.feature.label,
                format.amount(unit, costUsd: f.costUsd, tokens: f.inputTokens + f.outputTokens),
                hint: '${f.calls} calls · ${AiUsageFormat.tokens(f.inputTokens)} in / ${AiUsageFormat.tokens(f.outputTokens)} out',
              ),
            const SizedBox(height: 16),
            Text('By model', style: style),
            const SizedBox(height: 6),
            for (final m in usage.byModel)
              row(
                '${m.connectionName} · ${m.model}',
                m.priceKnown ? format.amount(unit, costUsd: m.costUsd, tokens: m.tokens) : (unit == AiUsageUnit.tokens ? format.amount(unit, costUsd: 0, tokens: m.tokens) : 'no price'),
                hint: m.priceKnown ? null : 'price not in the catalogue, so the limit counts it at a high fallback rate',
              ),
            const SizedBox(height: 16),
            Text('Most expensive applications this month', style: style),
            const SizedBox(height: 6),
            if (usage.bySubject.isEmpty) const HintText('None yet.'),
            for (final s in usage.bySubject)
              row(
                s.subject,
                format.amount(unit, costUsd: s.costUsd, tokens: s.tokens),
                hint: '${s.calls} call${s.calls == 1 ? '' : 's'}',
              ),
            const SizedBox(height: 8),
            const HintText(
              'Many attempts on one application usually means its script keeps failing validation; '
              'that is where the money goes.',
            ),
          ],
        ),
      ),
    );
  }
}
