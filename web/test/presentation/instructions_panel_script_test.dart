import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/domain/entities/upgrade_path.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/upgrade_path_usecases.dart';
import 'package:kintsugi_web/presentation/applications/widgets/instructions_panel.dart';

import 'instructions_panel_bloc_test.dart' show result;

class _Repo implements UpgradePathRepository {
  _Repo({this.stored, this.aiScript = '#!/bin/bash\necho ai'});

  UpgradePathResult? stored;
  final String aiScript;
  int saves = 0;
  int signs = 0;
  bool _asked = false;

  @override
  Future<UpgradePathPrompt> prompt({required String applicationName, String? platform, String? patchFailureId}) async =>
      UpgradePathPrompt(
        available: true,
        platform: 'macOS',
        prompt: 'Research it.',
        reason: null,
        existingResult: stored,
      );

  @override
  Future<UpgradePathResult> save(Map<String, dynamic> body) async {
    saves++;
    return stored = result(script: body['script'] as String?);
  }

  @override
  Future<UpgradePathResult> signScript({
    required String applicationName,
    required String platform,
    required String scriptSha256,
    String? patchFailureId,
  }) async {
    signs++;
    return stored = result(script: stored?.script, scriptSigned: true);
  }

  @override
  Future<RunStarted<UpgradePathRefreshStatus>> startRefresh({
    required String applicationName,
    String? platform,
    String? instructions,
  }) async {
    _asked = true;
    return const RunStarted(started: true, status: UpgradePathRefreshStatus.idle());
  }

  @override
  Future<UpgradePathRefreshStatus> refreshStatus(String applicationName) async => !_asked
      ? const UpgradePathRefreshStatus.idle()
      : UpgradePathRefreshStatus(
          isRunning: false,
          startedUtc: DateTime.utc(2026, 11, 4),
          completedUtc: DateTime.utc(2026, 11, 4),
          success: true,
          errorMessage: null,
          results: [stored = result(script: aiScript)],
        );

  @override
  dynamic noSuchMethod(Invocation invocation) => throw UnimplementedError();
}

const _signed = '''#!/bin/bash
set -e
# fetch
curl -fsSL https://download.example/app.dmg -o app.dmg
hdiutil attach app.dmg
cp -R /Volumes/App/App.app /Applications/
hdiutil detach /Volumes/App
echo done''';

const _changedAddress = '''#!/bin/bash
set -e
# fetch
curl -fsSL https://mirror.example/app.dmg -o app.dmg
hdiutil attach app.dmg
cp -R /Volumes/App/App.app /Applications/
hdiutil detach /Volumes/App
echo done''';

/// The script column of the panel: the buttons that act on the box, and the tab that shows what
/// changed since the row was opened.
void main() {
  late _Repo repo;
  late List<String> clipboard;

  setUp(() {
    clipboard = [];
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(
      SystemChannels.platform,
      (call) async {
        if (call.method == 'Clipboard.setData') clipboard.add((call.arguments as Map)['text'] as String);
        return null;
      },
    );
  });

  tearDown(() {
    locator.reset();
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(SystemChannels.platform, null);
  });

  Future<void> pumpPanel(WidgetTester tester, _Repo repository) async {
    repo = repository;
    locator
      ..registerSingleton(GetUpgradePathPrompt(repo))
      ..registerSingleton(StartUpgradePathRefresh(repo))
      ..registerSingleton(GetUpgradePathRefreshStatus(repo))
      ..registerSingleton(SaveUpgradePath(repo))
      ..registerSingleton(SignUpgradePathScript(repo));

    tester.view.physicalSize = const Size(1700, 2200);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        home: Scaffold(
          body: SingleChildScrollView(
            child: InstructionsPanel(applicationName: 'Nextcloud', platform: 'macOS', onServerStateChanged: () {}),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  Finder scriptBox() => find.byWidgetPredicate(
        (w) => w is TextField && (w.decoration?.hintText?.startsWith('Paste a result JSON') ?? false),
      );

  String boxText(WidgetTester tester) => tester.widget<TextField>(scriptBox()).controller!.text;

  Finder tab(String name) => find.byKey(ValueKey('script-tab-$name'));
  Finder button(String label) => find.widgetWithText(OutlinedButton, label.toUpperCase());

  bool enabled(WidgetTester tester, String label) => tester.widget<OutlinedButton>(button(label)).onPressed != null;

  Future<void> disposePanel(WidgetTester tester) => tester.pumpWidget(const SizedBox());

  group('the buttons that act on the box', () {
    testWidgets('are Copy and Clear, beside the Edit and Changes tabs', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));

      expect(find.byKey(const ValueKey('script-copy')), findsOneWidget);
      expect(find.byKey(const ValueKey('script-clear')), findsOneWidget);
      expect(tab('edit'), findsOneWidget);
      expect(tab('changes'), findsOneWidget);
      expect(find.text('UPDATE SCRIPT'), findsOneWidget);
      await disposePanel(tester);
    });

    testWidgets('Copy puts the whole script on the clipboard and says how long it was', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));

      await tester.tap(find.byKey(const ValueKey('script-copy')));
      await tester.pump();

      expect(clipboard.single, _signed);
      expect(find.text('Copied the whole script (8 lines).'), findsOneWidget);
      await disposePanel(tester);
    });

    testWidgets('Copy copies what is in the box now, edits and all', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));

      await tester.enterText(scriptBox(), '#!/bin/bash\necho typed');
      await tester.tap(find.byKey(const ValueKey('script-copy')));
      await tester.pump();

      expect(clipboard.single, '#!/bin/bash\necho typed');
      await disposePanel(tester);
    });

    testWidgets('Copy of an empty box says so rather than copying nothing', (tester) async {
      await pumpPanel(tester, _Repo());

      await tester.tap(find.byKey(const ValueKey('script-copy')));
      await tester.pump();

      expect(clipboard, isEmpty);
      expect(find.text('Nothing to copy: the box is empty.'), findsOneWidget);
      await disposePanel(tester);
    });
  });

  group('Clear', () {
    testWidgets('empties the box, puts the cursor in it, and offers Undo clear', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));

      await tester.tap(find.byKey(const ValueKey('script-clear')));
      await tester.pumpAndSettle();

      expect(boxText(tester), isEmpty);
      expect(tester.widget<TextField>(scriptBox()).focusNode!.hasFocus, isTrue, reason: 'so Ctrl+V pastes straight in');
      expect(find.byKey(const ValueKey('script-undo-clear')), findsOneWidget);
      await disposePanel(tester);
    });

    testWidgets('sends nothing to the server, and Save is held while the box is empty', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));

      await tester.tap(find.byKey(const ValueKey('script-clear')));
      await tester.pumpAndSettle();

      expect(repo.saves, 0);
      expect(repo.stored!.script, _signed);
      expect(enabled(tester, 'Save Script'), isFalse);
      expect(enabled(tester, 'Sign Script'), isFalse);
      await disposePanel(tester);
    });

    testWidgets('then pasting a new script makes Save available again', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));
      await tester.tap(find.byKey(const ValueKey('script-clear')));
      await tester.pumpAndSettle();

      await tester.enterText(scriptBox(), '#!/bin/bash\necho new');
      await tester.pumpAndSettle();

      expect(enabled(tester, 'Save Script'), isTrue);
      await disposePanel(tester);
    });

    testWidgets('Undo clear puts the script back and goes away', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));
      await tester.tap(find.byKey(const ValueKey('script-clear')));
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const ValueKey('script-undo-clear')));
      await tester.pumpAndSettle();

      expect(boxText(tester), _signed);
      expect(find.byKey(const ValueKey('script-undo-clear')), findsNothing);
      await disposePanel(tester);
    });

    testWidgets('has no Undo once something new has been pasted', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));
      await tester.tap(find.byKey(const ValueKey('script-clear')));
      await tester.pumpAndSettle();

      await tester.enterText(scriptBox(), 'pasted');
      await tester.pumpAndSettle();

      expect(find.byKey(const ValueKey('script-undo-clear')), findsNothing);
      await disposePanel(tester);
    });

    testWidgets('from the Changes tab goes back to the box to paste into', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));
      await tester.tap(tab('changes'));
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const ValueKey('script-clear')));
      await tester.pumpAndSettle();

      expect(scriptBox(), findsOneWidget, reason: 'the Edit tab, where the box is');
      await disposePanel(tester);
    });
  });

  group('the Changes tab', () {
    testWidgets('is dimmed for a row that had no script, and says why', (tester) async {
      await pumpPanel(tester, _Repo());

      await tester.tap(tab('changes'));
      await tester.pumpAndSettle();

      expect(scriptBox(), findsOneWidget, reason: 'still on the Edit tab');
      await disposePanel(tester);
    });

    testWidgets('says there are no changes while the script is as it was opened', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));

      await tester.tap(tab('changes'));
      await tester.pumpAndSettle();

      expect(find.textContaining('No changes: this is the same script'), findsOneWidget);
      await disposePanel(tester);
    });

    testWidgets('counts the changes on the tab as the box is edited', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));

      await tester.enterText(scriptBox(), _changedAddress);
      await tester.pumpAndSettle();

      expect(find.descendant(of: tab('changes'), matching: find.text('1')), findsOneWidget);
      await disposePanel(tester);
    });

    testWidgets('shows removed and added lines, with a warning for a changed download address', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));
      await tester.enterText(scriptBox(), _changedAddress);
      await tester.pumpAndSettle();

      await tester.tap(tab('changes'));
      await tester.pumpAndSettle();

      expect(find.textContaining('curl -fsSL https://download.example/app.dmg -o app.dmg'), findsOneWidget);
      expect(find.textContaining('curl -fsSL https://mirror.example/app.dmg -o app.dmg'), findsOneWidget);
      expect(find.textContaining('Download address changed: download.example → mirror.example'), findsOneWidget);
      expect(find.text('CHANGE 1 OF 1'), findsOneWidget);
      expect(find.text('As opened · signed'.toUpperCase()), findsOneWidget);
      expect(find.text('Now · unsaved edits'.toUpperCase()), findsOneWidget);
      await disposePanel(tester);
    });

    testWidgets('folds the unchanged lines, and Full script shows them', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));
      await tester.enterText(scriptBox(), _changedAddress);
      await tester.pumpAndSettle();
      await tester.tap(tab('changes'));
      await tester.pumpAndSettle();

      expect(find.textContaining('unchanged line'), findsOneWidget);
      expect(find.text('echo done'), findsNothing);

      await tester.tap(find.text('FULL SCRIPT'));
      await tester.pumpAndSettle();

      expect(find.textContaining('unchanged line'), findsNothing);
      expect(find.text('echo done'), findsOneWidget);
      await disposePanel(tester);
    });

    testWidgets('can be laid out side by side', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));
      await tester.enterText(scriptBox(), _changedAddress);
      await tester.pumpAndSettle();
      await tester.tap(tab('changes'));
      await tester.pumpAndSettle();

      await tester.tap(find.text('SIDE BY SIDE'));
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
      expect(find.textContaining('mirror.example/app.dmg'), findsOneWidget);
      await disposePanel(tester);
    });

    testWidgets('empty box: every line shows as removed, with a hint to paste', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));
      await tester.tap(find.byKey(const ValueKey('script-clear')));
      await tester.pumpAndSettle();

      await tester.tap(tab('changes'));
      await tester.pumpAndSettle();

      expect(find.textContaining('The box is empty. Paste the new script on the Edit tab'), findsOneWidget);
      expect(find.text('Now · box is empty'.toUpperCase()), findsOneWidget);
      await disposePanel(tester);
    });

    testWidgets('opens by itself when the AI hands back a script that differs from the signed one', (tester) async {
      await pumpPanel(
        tester,
        _Repo(stored: result(script: _signed, scriptSigned: true), aiScript: _changedAddress),
      );

      await tester.tap(find.text('SEND TO AI'));
      await tester.pump();
      await tester.pump(const Duration(seconds: 3));
      await tester.pumpAndSettle();

      expect(find.text('CHANGE 1 OF 1'), findsOneWidget);
      expect(scriptBox(), findsNothing);
      await disposePanel(tester);
    });
  });

  group('signing a script that differs from the signed one', () {
    Future<void> editAndSave(WidgetTester tester, String script) async {
      await tester.enterText(scriptBox(), script);
      await tester.pumpAndSettle();
      await tester.tap(button('Save Script'));
      await tester.pumpAndSettle();
    }

    testWidgets('is held until every change has been read, and says how many are left', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));

      await editAndSave(tester, _changedAddress);

      expect(enabled(tester, 'Sign Script'), isFalse);
      expect(find.textContaining('1 change left to read before signing'), findsOneWidget);
      await disposePanel(tester);
    });

    testWidgets('unlocks when the change is marked reviewed, and then signs', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));
      await editAndSave(tester, _changedAddress);

      await tester.tap(tab('changes'));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const ValueKey('review-0')));
      await tester.pumpAndSettle();

      expect(find.text('1 of 1 reviewed'), findsOneWidget);
      expect(enabled(tester, 'Sign Script'), isTrue);
      expect(find.textContaining('left to read'), findsNothing);

      await tester.tap(button('Sign Script'));
      await tester.pumpAndSettle();

      expect(repo.signs, 1);
      await disposePanel(tester);
    });

    testWidgets('locks again if the mark is taken back', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));
      await editAndSave(tester, _changedAddress);
      await tester.tap(tab('changes'));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const ValueKey('review-0')));
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const ValueKey('review-0')));
      await tester.pumpAndSettle();

      expect(enabled(tester, 'Sign Script'), isFalse);
      await disposePanel(tester);
    });

    testWidgets('is not held for a script saved identical to the signed one', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));

      await editAndSave(tester, '$_signed ');
      await tester.enterText(scriptBox(), _signed);
      await tester.pumpAndSettle();
      await tester.tap(button('Save Script'));
      await tester.pumpAndSettle();

      expect(enabled(tester, 'Sign Script'), isTrue);
      await disposePanel(tester);
    });

    testWidgets('is not held at all when the row opened with a script that was never signed', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed)));

      await editAndSave(tester, _changedAddress);

      expect(enabled(tester, 'Sign Script'), isTrue);
      expect(find.textContaining('left to read'), findsNothing);
      await tester.tap(tab('changes'));
      await tester.pumpAndSettle();
      expect(find.byKey(const ValueKey('review-0')), findsNothing, reason: 'a mark would gate nothing');
      await disposePanel(tester);
    });
  });

  /// The box used to be rewritten from the stored script whenever the state changed, and the first
  /// keystroke changes it (signing is held from then on). So the first thing typed or pasted into a
  /// row's script box was thrown away and the stored script came back.
  group('typing into the box', () {
    testWidgets('keeps what is typed or pasted on the very first edit', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true)));
      expect(boxText(tester), _signed);

      await tester.enterText(scriptBox(), '#!/bin/bash\necho pasted');
      await tester.pumpAndSettle();

      expect(boxText(tester), '#!/bin/bash\necho pasted');
      await disposePanel(tester);
    });

    testWidgets('keeps it across further edits', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed)));

      await tester.enterText(scriptBox(), 'one');
      await tester.pumpAndSettle();
      await tester.enterText(scriptBox(), 'one two');
      await tester.pumpAndSettle();

      expect(boxText(tester), 'one two');
      await disposePanel(tester);
    });

    testWidgets('still takes the AI\'s script when one arrives after an edit', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed), aiScript: '#!/bin/bash\necho from the ai'));
      await tester.enterText(scriptBox(), 'my own edit');
      await tester.pumpAndSettle();

      await tester.tap(find.text('SEND TO AI'));
      await tester.pump();
      await tester.pump(const Duration(seconds: 3));
      await tester.pumpAndSettle();

      expect(boxText(tester), '#!/bin/bash\necho from the ai');
      await disposePanel(tester);
    });

    testWidgets('shows what was saved, and leaves it there', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed)));
      await tester.enterText(scriptBox(), '#!/bin/bash\necho saved');
      await tester.pumpAndSettle();

      await tester.tap(button('Save Script'));
      await tester.pumpAndSettle();

      expect(repo.saves, 1);
      expect(boxText(tester), '#!/bin/bash\necho saved');
      await disposePanel(tester);
    });
  });

  /// Trojan Source and its relatives: text that reads one way and runs another. The Changes tab
  /// draws each such character where it is, and Sign asks first.
  group('hidden characters', () {
    // A right-to-left override inside a comment, the classic shape, and a zero-width space.
    const trojan = '#!/bin/bash\nset -e\n# check \u202E} ;fi\necho do\u200Bne';

    Future<void> sendToAi(WidgetTester tester) async {
      await tester.tap(find.text('SEND TO AI'));
      await tester.pump();
      await tester.pump(const Duration(seconds: 3));
      await tester.pumpAndSettle();
    }

    testWidgets('are drawn as highlighted escapes on the Changes tab, and flag the change', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed, scriptSigned: true), aiScript: trojan));
      await sendToAi(tester);

      expect(find.textContaining('<U+202E>'), findsOneWidget);
      expect(find.textContaining('<U+200B>'), findsOneWidget);
      expect(find.textContaining('\u202E'), findsNothing, reason: 'never drawn as itself');
      expect(find.textContaining('Contains invisible or text-direction characters'), findsOneWidget);
      await disposePanel(tester);
    });

    testWidgets('a line-ending-only change is a change, with the \\r shown', (tester) async {
      await pumpPanel(
        tester,
        _Repo(stored: result(script: _signed, scriptSigned: true), aiScript: _signed.replaceAll('\n', '\r\n')),
      );
      await sendToAi(tester);

      expect(find.text('No changes: this is the same script that was here when you opened this row.'), findsNothing);
      expect(find.textContaining('Only the line ending changed'), findsOneWidget);
      expect(find.textContaining(r'\r'), findsWidgets);
      await disposePanel(tester);
    });

    testWidgets('are warned about beside Sign, with where they are', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: trojan)));

      final warning = find.byKey(const ValueKey('script-invisible-warning'));
      expect(warning, findsOneWidget);
      final text = tester.widget<Text>(warning).data!;
      expect(text, contains('2 hidden characters'));
      expect(text, contains('U+202E right-to-left override (line 3)'));
      expect(text, contains('U+200B zero-width space (line 4)'));
      await disposePanel(tester);
    });

    testWidgets('make Sign ask first, and Cancel signs nothing', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: trojan)));
      expect(enabled(tester, 'Sign Script'), isTrue);

      await tester.tap(button('Sign Script'));
      await tester.pumpAndSettle();
      expect(find.text('Sign a script with hidden characters?'), findsOneWidget);

      await tester.tap(find.text('CANCEL'));
      await tester.pumpAndSettle();
      expect(repo.signs, 0);
      await disposePanel(tester);
    });

    testWidgets('Sign anyway signs', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: trojan)));

      await tester.tap(button('Sign Script'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('SIGN ANYWAY'));
      await tester.pumpAndSettle();

      expect(repo.signs, 1);
      await disposePanel(tester);
    });

    testWidgets('an ordinary script signs without the question', (tester) async {
      await pumpPanel(tester, _Repo(stored: result(script: _signed)));

      expect(find.byKey(const ValueKey('script-invisible-warning')), findsNothing);
      await tester.tap(button('Sign Script'));
      await tester.pumpAndSettle();

      expect(find.text('Sign a script with hidden characters?'), findsNothing);
      expect(repo.signs, 1);
      await disposePanel(tester);
    });
  });
}
