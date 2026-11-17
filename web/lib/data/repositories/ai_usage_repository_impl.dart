import '../../core/network/api_client.dart';
import '../../domain/entities/ai_usage.dart';
import '../../domain/repositories/repositories.dart';
import '../models/ai_usage_mapper.dart';

class AiUsageRepositoryImpl implements AiUsageRepository {
  const AiUsageRepositoryImpl(this._api);

  final ApiClient _api;

  @override
  Future<AiUsage> read() async =>
      aiUsageFromJson(await _api.getJson('/api/ai-settings/usage') as Map<String, dynamic>);

  @override
  Future<AiBudgetSettings> saveBudget(AiBudgetSettings settings) async => aiBudgetSettingsFromJson(
        await _api.putJson('/api/ai-settings/budget', body: {
          'budgetAmount': settings.budgetAmount,
          'budgetCurrency': settings.budgetCurrency,
          'usdToAudRate': settings.usdToAudRate,
          'warnPercent': settings.warnPercent,
          'stopAtLimit': settings.stopAtLimit,
        }) as Map<String, dynamic>,
      );
}
