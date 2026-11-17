import 'package:equatable/equatable.dart';

/// How the AI Usage screen shows amounts. A display choice, kept by the screen: the server only
/// ever speaks US dollars and tokens.
enum AiUsageUnit {
  usd('USD'),
  aud('AUD'),
  tokens('Tokens');

  const AiUsageUnit(this.label);

  final String label;
}

/// Mirrors `AiFeature`. Sent as an ordinal.
enum AiUsageFeature {
  scriptResearch('Script research'),
  scriptRepair('Script repair'),
  cpeSuggestion('CPE suggestion');

  const AiUsageFeature(this.label);

  final String label;
}

/// Where the month's spending stands against the limit. Mirrors `AiBudgetStatusDto`.
class AiBudgetStatus extends Equatable {
  const AiBudgetStatus({
    required this.spentUsd,
    required this.budgetUsd,
    required this.percent,
    required this.warnReached,
    required this.limitReached,
    required this.blocked,
    required this.message,
    this.unpricedCalls = 0,
    this.unpricedEstimateUsd = 0,
  });

  /// Includes [unpricedEstimateUsd]: calls to a model the catalogue has no price for are charged
  /// against the limit at a deliberately high fallback rate (`AiPricing.FallbackInputPerMillion`)
  /// rather than as nothing, so an unpriced model is not a way around the cap.
  final double spentUsd;

  /// Null when no limit is set.
  final double? budgetUsd;
  final int percent;
  final bool warnReached;
  final bool limitReached;

  /// New AI calls are being refused: the limit is reached and the stop is on.
  final bool blocked;
  final String? message;

  /// Calls this month to a model with no catalogue price.
  final int unpricedCalls;

  /// The part of [spentUsd] that is those calls at the fallback rate.
  final double unpricedEstimateUsd;

  @override
  List<Object?> get props =>
      [spentUsd, budgetUsd, percent, warnReached, limitReached, blocked, message, unpricedCalls, unpricedEstimateUsd];
}

/// Mirrors `AiBudgetSettingsDto`.
class AiBudgetSettings extends Equatable {
  const AiBudgetSettings({
    required this.budgetAmount,
    required this.budgetCurrency,
    required this.usdToAudRate,
    required this.warnPercent,
    required this.stopAtLimit,
  });

  /// In [budgetCurrency]; null for no limit.
  final double? budgetAmount;
  final String budgetCurrency;
  final double usdToAudRate;
  final int warnPercent;
  final bool stopAtLimit;

  @override
  List<Object?> get props => [budgetAmount, budgetCurrency, usdToAudRate, warnPercent, stopAtLimit];
}

class AiUsageFeatureRow extends Equatable {
  const AiUsageFeatureRow({
    required this.feature,
    required this.calls,
    required this.inputTokens,
    required this.outputTokens,
    required this.costUsd,
  });

  final AiUsageFeature feature;
  final int calls;
  final int inputTokens;
  final int outputTokens;
  final double costUsd;

  @override
  List<Object?> get props => [feature, calls, inputTokens, outputTokens, costUsd];
}

class AiUsageModelRow extends Equatable {
  const AiUsageModelRow({
    required this.connectionName,
    required this.model,
    required this.calls,
    required this.inputTokens,
    required this.outputTokens,
    required this.costUsd,
    required this.priceKnown,
  });

  final String connectionName;
  final String model;
  final int calls;
  final int inputTokens;
  final int outputTokens;
  final double costUsd;

  /// False when some call had no price, so [costUsd] undercounts. Distinct from "free".
  final bool priceKnown;

  int get tokens => inputTokens + outputTokens;

  @override
  List<Object?> get props => [connectionName, model, calls, inputTokens, outputTokens, costUsd, priceKnown];
}

class AiUsageSubjectRow extends Equatable {
  const AiUsageSubjectRow({required this.subject, required this.calls, required this.tokens, required this.costUsd});

  final String subject;
  final int calls;
  final int tokens;
  final double costUsd;

  @override
  List<Object?> get props => [subject, calls, tokens, costUsd];
}

class AiUsageDay extends Equatable {
  const AiUsageDay({required this.day, required this.tokens, required this.costUsd});

  final DateTime day;
  final int tokens;
  final double costUsd;

  @override
  List<Object?> get props => [day, tokens, costUsd];
}

/// What the AI Usage screen shows. Mirrors `AiUsageDto`. Costs are estimates from the tokens each
/// provider reported and the models.dev price list.
class AiUsage extends Equatable {
  const AiUsage({
    required this.status,
    required this.settings,
    required this.monthStartUtc,
    required this.nextMonthStartUtc,
    required this.totalTokens,
    required this.byFeature,
    required this.byModel,
    required this.bySubject,
    required this.days,
  });

  final AiBudgetStatus status;
  final AiBudgetSettings settings;
  final DateTime monthStartUtc;
  final DateTime nextMonthStartUtc;
  final int totalTokens;
  final List<AiUsageFeatureRow> byFeature;
  final List<AiUsageModelRow> byModel;
  final List<AiUsageSubjectRow> bySubject;
  final List<AiUsageDay> days;

  @override
  List<Object?> get props =>
      [status, settings, monthStartUtc, nextMonthStartUtc, totalTokens, byFeature, byModel, bySubject, days];
}

/// How an amount reads in each [AiUsageUnit]. Pure, so what the screen prints can be tested.
class AiUsageFormat {
  const AiUsageFormat(this.usdToAudRate);

  final double usdToAudRate;

  String usd(double value) => '\$${value.toStringAsFixed(2)}';

  String aud(double value) => 'A\$${(value * usdToAudRate).toStringAsFixed(2)}';

  /// 1,234 → "1K", 2,500,000 → "2.5M": a count a person can scan.
  static String tokens(num value) {
    if (value >= 1000000) return '${(value / 1000000).toStringAsFixed(1)}M';
    if (value >= 1000) return '${(value / 1000).round()}K';
    return value.round().toString();
  }

  /// A cost in the chosen unit, or the tokens when that is what is shown.
  String amount(AiUsageUnit unit, {required double costUsd, required num tokens}) => switch (unit) {
        AiUsageUnit.usd => usd(costUsd),
        AiUsageUnit.aud => aud(costUsd),
        AiUsageUnit.tokens => '${AiUsageFormat.tokens(tokens)} tokens',
      };
}
