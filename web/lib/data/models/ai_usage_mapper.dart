import '../../core/network/json_reader.dart';
import '../../domain/entities/ai_usage.dart';

double _d(Object? value) => (value as num?)?.toDouble() ?? 0;
int _i(Object? value) => (value as num?)?.toInt() ?? 0;

AiUsageFeature _feature(Object? raw) => enumFromJson(
      raw,
      AiUsageFeature.values,
      const ['ScriptResearch', 'ScriptRepair', 'CpeSuggestion'],
      AiUsageFeature.scriptResearch,
    );

AiBudgetStatus aiBudgetStatusFromJson(Map<String, dynamic> json) => AiBudgetStatus(
      spentUsd: _d(json['spentUsd']),
      budgetUsd: (json['budgetUsd'] as num?)?.toDouble(),
      percent: _i(json['percent']),
      warnReached: json['warnReached'] as bool? ?? false,
      limitReached: json['limitReached'] as bool? ?? false,
      blocked: json['blocked'] as bool? ?? false,
      message: json['message'] as String?,
      unpricedCalls: _i(json['unpricedCalls']),
      unpricedEstimateUsd: _d(json['unpricedEstimateUsd']),
    );

AiBudgetSettings aiBudgetSettingsFromJson(Map<String, dynamic> json) => AiBudgetSettings(
      budgetAmount: (json['budgetAmount'] as num?)?.toDouble(),
      budgetCurrency: json['budgetCurrency'] as String? ?? 'USD',
      usdToAudRate: _d(json['usdToAudRate']) == 0 ? 1.55 : _d(json['usdToAudRate']),
      warnPercent: _i(json['warnPercent']) == 0 ? 80 : _i(json['warnPercent']),
      stopAtLimit: json['stopAtLimit'] as bool? ?? true,
    );

/// Reads an `AiUsageDto`. Hand-mirrored — see `.claude/rules/hand-mirrored-dtos.md`.
AiUsage aiUsageFromJson(Map<String, dynamic> json) => AiUsage(
      status: aiBudgetStatusFromJson(json['status'] as Map<String, dynamic>? ?? const {}),
      settings: aiBudgetSettingsFromJson(json['settings'] as Map<String, dynamic>? ?? const {}),
      monthStartUtc: dateTimeRequiredFromJson(json['monthStartUtc']),
      nextMonthStartUtc: dateTimeRequiredFromJson(json['nextMonthStartUtc']),
      totalTokens: _i(json['totalTokens']),
      byFeature: listFromJson(
        json['byFeature'],
        (e) => AiUsageFeatureRow(
          feature: _feature(e['feature']),
          calls: _i(e['calls']),
          inputTokens: _i(e['inputTokens']),
          outputTokens: _i(e['outputTokens']),
          costUsd: _d(e['costUsd']),
        ),
      ),
      byModel: listFromJson(
        json['byModel'],
        (e) => AiUsageModelRow(
          connectionName: e['connectionName'] as String? ?? '',
          model: e['model'] as String? ?? '',
          calls: _i(e['calls']),
          inputTokens: _i(e['inputTokens']),
          outputTokens: _i(e['outputTokens']),
          costUsd: _d(e['costUsd']),
          priceKnown: e['priceKnown'] as bool? ?? false,
        ),
      ),
      bySubject: listFromJson(
        json['bySubject'],
        (e) => AiUsageSubjectRow(
          subject: e['subject'] as String? ?? '',
          calls: _i(e['calls']),
          tokens: _i(e['tokens']),
          costUsd: _d(e['costUsd']),
        ),
      ),
      days: listFromJson(
        json['days'],
        (e) => AiUsageDay(
          day: DateTime.tryParse(e['day'] as String? ?? '') ?? DateTime.fromMillisecondsSinceEpoch(0),
          tokens: _i(e['tokens']),
          costUsd: _d(e['costUsd']),
        ),
      ),
    );
