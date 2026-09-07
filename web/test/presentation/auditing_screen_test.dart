import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/buttons.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/settings.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/settings_usecases.dart';
import 'package:kintsugi_web/presentation/settings/auditing_screen.dart';

/// The Auditing settings screen, pumped against a fake repository the way `injection.dart`
/// registers the real one.
///
/// What this pins is the part a seven-way provider switch makes easy to break and nothing else
/// checks: that **every** provider lays its own fields and its own instructions out without
/// throwing or overflowing. `flutter analyze` cannot see a layout, and the other settings screens
/// have one field set each — this one has seven, and only the selected one is ever built.
///
/// It also pins the two rules the form carries on the secret, both of which are security
/// behaviour rather than presentation: a stored secret is only offered as keepable while the
/// provider it was issued for is still selected, and the box is never repopulated.
void main() {
  late FakeAuditSettingsRepository repository;

  AuditSettings settings({
    AuditProvider provider = AuditProvider.datadog,
    bool hasSecret = false,
    String? region,
    String? endpoint,
    String? clientId,
  }) =>
      AuditSettings(
        provider: provider,
        isEnabled: false,
        endpoint: endpoint,
        region: region,
        clientId: clientId,
        hasSecret: hasSecret,
        tenantId: null,
        projectId: null,
        logGroup: null,
        dataCollectionRuleId: null,
        stream: null,
        index: null,
      );

  void register(AuditSettings stored) {
    repository = FakeAuditSettingsRepository(stored);
    locator
      ..registerSingleton(GetAuditSettings(repository))
      ..registerSingleton(UpdateAuditSettings(repository));
  }

  tearDown(() => locator.reset());

  Future<void> pumpScreen(WidgetTester tester, {Size size = const Size(1400, 1400)}) async {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        home: const Scaffold(body: AuditingSettingsScreen()),
      ),
    );
    await tester.pumpAndSettle();
  }

  Future<void> choose(WidgetTester tester, AuditProvider provider) async {
    await tester.tap(find.byType(DropdownButton<AuditProvider>).first);
    await tester.pumpAndSettle();
    await tester.tap(find.text(provider.label).last);
    await tester.pumpAndSettle();
  }

  for (final provider in AuditProvider.values) {
    testWidgets('${provider.name} lays out its fields and its instructions', (tester) async {
      register(settings());
      await pumpScreen(tester);
      await choose(tester, provider);

      // A provider whose fields or instructions overflowed would have thrown by now; pumping the
      // real widget at a real size is the only thing that finds that.
      expect(tester.takeException(), isNull);
      expect(find.text('Setting this up'.toUpperCase()), findsOneWidget);
      expect(find.byType(PrimaryButton), findsOneWidget);
    });
  }

  testWidgets('lays out at a narrow width, where the instructions stack under the form',
      (tester) async {
    register(settings(provider: AuditProvider.azureMonitor, hasSecret: true));
    // Azure asks for the most fields and carries the longest instructions, so it is the case that
    // overflows first. SettingsColumns stacks below 640 logical pixels.
    await pumpScreen(tester, size: const Size(600, 2400));

    expect(tester.takeException(), isNull);
    // LabelledField uppercases, as SettingsAside and PrimaryButton do.
    expect(find.text('Data collection rule ID'.toUpperCase()), findsOneWidget);
  });

  testWidgets('offers to keep a stored secret only while its own provider is selected',
      (tester) async {
    register(settings(hasSecret: true, region: 'datadoghq.eu'));
    await pumpScreen(tester);

    expect(find.textContaining('already configured'), findsOneWidget);

    // The server drops the secret on a provider change — it was issued by Datadog and must not be
    // sent to AWS — so the offer to keep it has to go with it.
    await choose(tester, AuditProvider.awsCloudWatch);

    expect(find.textContaining('already configured'), findsNothing);
    expect(find.textContaining('Changing the platform drops the stored secret'), findsOneWidget);
  });

  testWidgets('never repopulates the secret box, and sends null when it is left blank',
      (tester) async {
    register(settings(hasSecret: true, region: 'datadoghq.eu'));
    await pumpScreen(tester);

    // The value was never sent to this client, so there is nothing to put back.
    final secretBox = tester.widget<TextField>(find.byType(TextField).last);
    expect(secretBox.controller?.text, isEmpty);

    await tester.tap(find.byType(PrimaryButton));
    await tester.pumpAndSettle();

    // Blank means "keep the stored one", which is null on the wire rather than an empty string —
    // an empty string would read as a secret the server then refuses as too short.
    expect(repository.lastSecret, isNull);
    expect(repository.lastClearSecret, isFalse);
    expect(find.text('Settings saved.'), findsOneWidget);
  });

  testWidgets('a provider whose secret is optional can have the stored one removed', (tester) async {
    register(settings(
      provider: AuditProvider.grafanaLoki,
      hasSecret: true,
      endpoint: 'https://logs-prod-1.grafana.net',
      clientId: '123456',
    ));
    await pumpScreen(tester);

    await tester.tap(find.text('Remove the stored token'));
    await tester.pumpAndSettle();
    await tester.tap(find.byType(PrimaryButton));
    await tester.pumpAndSettle();

    // Blank cannot mean both "keep" and "remove", which is the whole reason for the flag.
    expect(repository.lastClearSecret, isTrue);
    expect(repository.lastSecret, isNull);
  });

  testWidgets('a hosted provider offers no way to remove the secret', (tester) async {
    register(settings(hasSecret: true, region: 'datadoghq.eu'));
    await pumpScreen(tester);

    // Datadog cannot be saved without a key, so a checkbox promising to remove one would be an
    // offer the server refuses. AuditSettings.RequiresSecret is the rule, mirrored on this side.
    expect(find.text('Remove the stored token'), findsNothing);
  });
}

class FakeAuditSettingsRepository implements AuditSettingsRepository {
  FakeAuditSettingsRepository(this._stored);

  AuditSettings _stored;

  String? lastSecret;
  bool? lastClearSecret;

  @override
  Future<AuditSettings> read() async => _stored;

  @override
  Future<AuditSettings> update({
    required AuditProvider provider,
    required bool isEnabled,
    required String? endpoint,
    required String? region,
    required String? clientId,
    required String? secret,
    required bool clearSecret,
    required String? tenantId,
    required String? projectId,
    required String? logGroup,
    required String? dataCollectionRuleId,
    required String? stream,
    required String? index,
  }) async {
    lastSecret = secret;
    lastClearSecret = clearSecret;
    _stored = AuditSettings(
      provider: provider,
      isEnabled: isEnabled,
      endpoint: endpoint,
      region: region,
      clientId: clientId,
      hasSecret: clearSecret ? false : _stored.hasSecret || secret != null,
      tenantId: tenantId,
      projectId: projectId,
      logGroup: logGroup,
      dataCollectionRuleId: dataCollectionRuleId,
      stream: stream,
      index: index,
    );
    return _stored;
  }
}
