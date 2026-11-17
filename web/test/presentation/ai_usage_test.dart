import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/buttons.dart';
import 'package:kintsugi_web/data/models/ai_usage_mapper.dart';
import 'package:kintsugi_web/data/models/upgrade_path_mapper.dart';
import 'package:kintsugi_web/domain/entities/ai_usage.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/ai_usage_usecases.dart';
import 'package:kintsugi_web/presentation/applications/run_progress_mappers.dart';
import 'package:kintsugi_web/presentation/settings/ai_usage_screen.dart';

Map<String, dynamic> _json({double spent = 18.4, double? budget = 50, int percent = 36, bool blocked = false, bool limit = false, int unpricedCalls = 0, double unpricedUsd = 0}) => {
      'status': {
        'spentUsd': spent,
        'budgetUsd': budget,
        'percent': percent,
        'warnReached': percent >= 80,
        'limitReached': limit,
        'blocked': blocked,
        'message': blocked ? 'AI budget reached' : null,
        'unpricedCalls': unpricedCalls,
        'unpricedEstimateUsd': unpricedUsd,
      },
      'settings': {'budgetAmount': budget, 'budgetCurrency': 'USD', 'usdToAudRate': 1.55, 'warnPercent': 80, 'stopAtLimit': true},
      'monthStartUtc': '2026-11-01T00:00:00Z',
      'nextMonthStartUtc': '2026-12-01T00:00:00Z',
      'totalTokens': 2500000,
      'byFeature': [
        {'feature': 0, 'calls': 214, 'inputTokens': 1900000, 'outputTokens': 310000, 'costUsd': 12.6},
        {'feature': 'ScriptRepair', 'calls': 61, 'inputTokens': 420000, 'outputTokens': 96000, 'costUsd': 3.9},
      ],
      'byModel': [
        {'connectionName': 'Anthropic', 'model': 'claude-sonnet-5-5', 'calls': 275, 'inputTokens': 2300000, 'outputTokens': 400000, 'costUsd': 16.5, 'priceKnown': true},
        {'connectionName': 'Other', 'model': 'mystery', 'calls': 2, 'inputTokens': 1000, 'outputTokens': 100, 'costUsd': 0, 'priceKnown': false},
      ],
      'bySubject': [
        {'subject': 'Microsoft Teams', 'calls': 17, 'tokens': 900000, 'costUsd': 4.2},
      ],
      'days': [
        {'day': '2026-10-30', 'tokens': 0, 'costUsd': 0},
        {'day': '2026-10-31', 'tokens': 100000, 'costUsd': 1.0},
      ],
    };

class _FakeUsage implements AiUsageRepository {
  _FakeUsage(this.json);

  Map<String, dynamic> json;
  final saved = <AiBudgetSettings>[];

  @override
  Future<AiUsage> read() async => aiUsageFromJson(json);

  @override
  Future<AiBudgetSettings> saveBudget(AiBudgetSettings settings) async {
    saved.add(settings);
    return settings;
  }
}

void main() {
  group('reading the server', () {
    test('features arrive as an ordinal or a name, and prices unknown stay unknown', () {
      final usage = aiUsageFromJson(_json());

      expect(usage.byFeature.map((f) => f.feature), [AiUsageFeature.scriptResearch, AiUsageFeature.scriptRepair]);
      expect(usage.byModel.last.priceKnown, isFalse);
      expect(usage.status.budgetUsd, 50);
      expect(usage.days.last.costUsd, 1.0);
    });

    test('the unpriced share of the spend is read, and an older server without it reads as none', () {
      final status = aiUsageFromJson(_json(unpricedCalls: 3, unpricedUsd: 1.25)).status;
      expect((status.unpricedCalls, status.unpricedEstimateUsd), (3, 1.25));

      final older = aiBudgetStatusFromJson({'spentUsd': 1, 'percent': 0});
      expect((older.unpricedCalls, older.unpricedEstimateUsd), (0, 0.0));
    });

    test('no budget reads as null, not zero', () {
      expect(aiUsageFromJson(_json(budget: null)).status.budgetUsd, isNull);
    });

    test('the scan status carries the budget message through to the run progress', () {
      final blocked = scanStatusFromJson({'isRunning': false, 'aiBudgetMessage': 'AI budget reached: ...'});
      final fine = scanStatusFromJson({'isRunning': false});

      expect(scanProgress(blocked).blockedReason, 'AI budget reached: ...');
      expect(scanProgress(fine).blockedReason, isNull);
    });
  });

  group('formatting', () {
    const format = AiUsageFormat(1.55);

    test('dollars, Australian dollars at the rate, and tokens in a form a person can scan', () {
      expect(format.usd(18.4), '\$18.40');
      expect(format.aud(10), 'A\$15.50');
      expect(AiUsageFormat.tokens(2500000), '2.5M');
      expect(AiUsageFormat.tokens(310000), '310K');
      expect(AiUsageFormat.tokens(640), '640');
    });

    test('one amount reads three ways', () {
      expect(format.amount(AiUsageUnit.usd, costUsd: 10, tokens: 2000000), '\$10.00');
      expect(format.amount(AiUsageUnit.aud, costUsd: 10, tokens: 2000000), 'A\$15.50');
      expect(format.amount(AiUsageUnit.tokens, costUsd: 10, tokens: 2000000), '2.0M tokens');
    });
  });

  group('the screen', () {
    late _FakeUsage repository;

    tearDown(() => locator.reset());

    Future<void> pump(WidgetTester tester, Map<String, dynamic> json) async {
      repository = _FakeUsage(json);
      locator
        ..registerSingleton(GetAiUsage(repository))
        ..registerSingleton(SaveAiBudget(repository));

      tester.view.physicalSize = const Size(1500, 2200);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);

      await tester.pumpWidget(MaterialApp(theme: AppTheme.light(), home: const Scaffold(body: AiUsageScreen())));
      await tester.pumpAndSettle();
    }

    testWidgets('shows the month against the budget, in dollars to begin with', (tester) async {
      await pump(tester, _json());

      expect(find.text('\$18.40'), findsOneWidget);
      expect(find.textContaining('of \$50.00 budget (36%)'), findsOneWidget);
      expect(find.text('Microsoft Teams'), findsOneWidget);
    });

    testWidgets('one click changes every amount to Australian dollars, and another to tokens', (tester) async {
      await pump(tester, _json());

      await tester.tap(find.widgetWithText(SecondaryButton, 'AUD'));
      await tester.pumpAndSettle();
      expect(find.text('A\$28.52'), findsOneWidget, reason: '18.40 x 1.55');
      expect(find.textContaining('of A\$77.50 budget'), findsOneWidget);

      await tester.tap(find.widgetWithText(SecondaryButton, 'TOKENS'));
      await tester.pumpAndSettle();
      expect(find.text('2.5M tokens'), findsOneWidget);
      expect(find.textContaining('about \$18.40 of a \$50.00 budget'), findsOneWidget, reason: 'the limit is still money');
    });

    testWidgets('at the limit with the stop on it says new AI calls are refused', (tester) async {
      await pump(tester, _json(spent: 50, percent: 100, blocked: true, limit: true));

      expect(find.textContaining('New AI calls are being refused until December 1'), findsOneWidget);
    });

    testWidgets('a model with no catalogue price is said so, not shown as free', (tester) async {
      await pump(tester, _json());

      expect(find.text('no price'), findsOneWidget);
      expect(find.textContaining('price not in the catalogue'), findsOneWidget);
    });

    testWidgets('saving sends the budget, its currency, the rate, the warning and the stop', (tester) async {
      await pump(tester, _json());

      await tester.tap(find.text('SAVE'));
      await tester.pumpAndSettle();

      expect(repository.saved, hasLength(1));
      final saved = repository.saved.single;
      expect((saved.budgetAmount, saved.budgetCurrency, saved.usdToAudRate, saved.warnPercent, saved.stopAtLimit),
          (50.0, 'USD', 1.55, 80, true));
    });

    testWidgets('a budget that is not a number is refused before anything is sent', (tester) async {
      await pump(tester, _json());

      await tester.enterText(find.widgetWithText(TextField, '50').first, 'lots');
      await tester.tap(find.text('SAVE'));
      await tester.pumpAndSettle();

      expect(repository.saved, isEmpty);
      expect(find.textContaining('must be a number above zero'), findsOneWidget);
    });

    testWidgets('a sub-cent budget is refused before anything is sent, since the server stores cents', (tester) async {
      await pump(tester, _json());

      for (final text in ['0.004', '12.345']) {
        await tester.enterText(find.widgetWithText(TextField, '50').first, text);
        await tester.tap(find.text('SAVE'));
        await tester.pumpAndSettle();
        expect(find.textContaining('at least 0.01 with no more than two decimal places'), findsOneWidget, reason: text);
        await tester.enterText(find.widgetWithText(TextField, text).first, '50');
      }
      expect(repository.saved, isEmpty);

      await tester.enterText(find.widgetWithText(TextField, '50').first, '0.01');
      await tester.tap(find.text('SAVE'));
      await tester.pumpAndSettle();
      expect(repository.saved.single.budgetAmount, 0.01);
    });

    testWidgets('spend on unpriced models is said to be counted at a fallback rate', (tester) async {
      await pump(tester, _json(unpricedCalls: 2, unpricedUsd: 0.02));

      expect(find.textContaining('Includes about \$0.02 for 2 calls to models with no catalogue price'), findsOneWidget);
    });

    testWidgets('with every model priced there is no fallback line', (tester) async {
      await pump(tester, _json());

      expect(find.textContaining('fallback rate so they still count'), findsNothing);
    });
  });
}
