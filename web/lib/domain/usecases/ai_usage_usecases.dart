import '../entities/ai_usage.dart';
import '../repositories/repositories.dart';

/// This month's AI spending against the limit, with the breakdowns.
class GetAiUsage {
  const GetAiUsage(this._repository);

  final AiUsageRepository _repository;

  Future<AiUsage> call() => _repository.read();
}

/// Sets the monthly AI budget, its warning level, the exchange rate and whether to stop at the limit.
class SaveAiBudget {
  const SaveAiBudget(this._repository);

  final AiUsageRepository _repository;

  Future<AiBudgetSettings> call(AiBudgetSettings settings) => _repository.saveBudget(settings);
}
