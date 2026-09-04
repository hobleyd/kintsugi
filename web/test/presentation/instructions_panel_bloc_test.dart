import 'dart:convert';

import 'package:bloc_test/bloc_test.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/upgrade_path.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/upgrade_path_usecases.dart';
import 'package:kintsugi_web/presentation/applications/instructions_panel_bloc.dart';

/// A stand-in for the API that records what the save route was sent.
///
/// Hand-written rather than generated: what these tests are about is the exact body, so a fake
/// that simply keeps it is clearer than a mock with matchers over it.
class FakeUpgradePathRepository implements UpgradePathRepository {
  FakeUpgradePathRepository({required this.promptResult});

  final UpgradePathPrompt promptResult;

  Map<String, dynamic>? savedBody;
  String? signedApplication;
  String? signedPlatform;

  @override
  Future<UpgradePathPrompt> prompt({required String applicationName, String? platform}) async =>
      promptResult;

  @override
  Future<UpgradePathResult> save(Map<String, dynamic> body) async {
    savedBody = body;
    return result(
      applicationName: body['applicationName'] as String? ?? '',
      platform: body['platform'] as String? ?? '',
      script: body['script'] as String?,
    );
  }

  @override
  Future<UpgradePathResult> signScript({
    required String applicationName,
    required String platform,
  }) async {
    signedApplication = applicationName;
    signedPlatform = platform;
    return result(
      applicationName: applicationName,
      platform: platform,
      script: '#!/bin/bash',
      scriptSigned: true,
    );
  }

  @override
  Future<UpgradePathRefreshStatus> refreshStatus(String applicationName) async =>
      const UpgradePathRefreshStatus.idle();

  @override
  Future<RunStarted<UpgradePathRefreshStatus>> startRefresh({
    required String applicationName,
    String? platform,
    String? instructions,
  }) async =>
      const RunStarted(started: true, status: UpgradePathRefreshStatus.idle());

  @override
  Future<RunStarted<UpgradePathScanStatus>> startScan() async =>
      const RunStarted(started: true, status: UpgradePathScanStatus.idle());

  @override
  Future<UpgradePathScanStatus> scanStatus() async => const UpgradePathScanStatus.idle();

  @override
  Future<RunStarted<UpdateCheckStatus>> startUpdateCheck() async =>
      const RunStarted(started: true, status: UpdateCheckStatus.idle());

  @override
  Future<UpdateCheckStatus> updateCheckStatus() async => const UpdateCheckStatus.idle();

  @override
  Future<UpdateCheckResult> checkUpdate({
    required String applicationName,
    required String platform,
  }) async =>
      UpdateCheckResult(
        applicationName: applicationName,
        platform: platform,
        success: true,
        versionChanged: false,
        note: null,
      );
}

UpgradePathResult result({
  String applicationName = 'Nextcloud',
  String platform = 'macOS',
  String? script = '#!/bin/bash',
  bool scriptSigned = false,
  Map<String, dynamic>? raw,
}) =>
    UpgradePathResult(
      applicationName: applicationName,
      platform: platform,
      status: UpgradePathStatus.found,
      latestVersion: '3.16.0',
      method: UpgradeMethod.script,
      downloadUrl: null,
      command: null,
      instructions: null,
      sourceUrl: 'https://nextcloud.com',
      notes: 'Researched by the AI agent.',
      checkedUtc: DateTime.utc(2026, 9, 1),
      script: script,
      scriptSigned: scriptSigned,
      approvalOutcome: null,
      approvalPullRequestUrl: null,
      approvalMessage: null,
      raw: raw ??
          {
            'applicationName': applicationName,
            'platform': platform,
            'status': 'Found',
            'latestVersion': '3.16.0',
            'method': 'Script',
            'notes': 'Researched by the AI agent.',
            'sourceUrl': 'https://nextcloud.com',
            'script': script,
          },
    );

InstructionsPanelBloc blocFor(FakeUpgradePathRepository repository, {String platform = 'macOS'}) =>
    InstructionsPanelBloc(
      applicationName: 'Nextcloud',
      platform: platform,
      getPrompt: GetUpgradePathPrompt(repository),
      startRefresh: StartUpgradePathRefresh(repository),
      refreshStatus: GetUpgradePathRefreshStatus(repository),
      saveUpgradePath: SaveUpgradePath(repository),
      signScript: SignUpgradePathScript(repository),
    );

void main() {
  late FakeUpgradePathRepository repository;

  setUp(() {
    repository = FakeUpgradePathRepository(
      promptResult: UpgradePathPrompt(
        available: true,
        platform: 'macOS',
        prompt: 'Research an upgrade path for Nextcloud.',
        reason: null,
        existingResult: result(),
      ),
    );
  });

  group('opening the panel', () {
    blocTest<InstructionsPanelBloc, InstructionsPanelState>(
      'shows the stored result rather than an empty box, and allows signing it',
      build: () => blocFor(repository),
      act: (bloc) => bloc.add(const PanelOpened()),
      wait: const Duration(milliseconds: 10),
      verify: (bloc) {
        // An already-successful check shows up by default, so nobody re-runs the AI unnecessarily.
        expect(bloc.state.editorText, '#!/bin/bash');
        expect(bloc.state.inSyncWithServer, isTrue);
        expect(bloc.state.canSign, isTrue);
      },
    );
  });

  group('signing', () {
    blocTest<InstructionsPanelBloc, InstructionsPanelState>(
      'is refused the moment the editor is touched, and allowed again after a save',
      build: () => blocFor(repository),
      act: (bloc) async {
        bloc.add(const PanelOpened());
        await Future<void>.delayed(const Duration(milliseconds: 10));
        bloc.add(const EditorTouched());
        await Future<void>.delayed(const Duration(milliseconds: 10));
        expect(
          bloc.state.canSign,
          isFalse,
          reason: 'signing signs what the server holds, not what is on screen',
        );
        bloc.add(const ScriptSaveRequested('#!/bin/bash\n# edited'));
      },
      wait: const Duration(milliseconds: 20),
      verify: (bloc) => expect(bloc.state.canSign, isTrue),
    );

    blocTest<InstructionsPanelBloc, InstructionsPanelState>(
      'sends no script content, only the row it applies to',
      build: () => blocFor(repository),
      act: (bloc) async {
        bloc.add(const PanelOpened());
        await Future<void>.delayed(const Duration(milliseconds: 10));
        bloc.add(const ScriptSignRequested());
      },
      wait: const Duration(milliseconds: 20),
      verify: (_) {
        // A signature over text the client supplied would not be a review of what the fleet is
        // going to execute, which is the whole property the signing model rests on.
        expect(repository.signedApplication, 'Nextcloud');
        expect(repository.signedPlatform, 'macOS');
      },
    );

    test('the in-flight state announces itself rather than only disabling the button', () {
      // `_onSign` clears the previous outcome and sets "Signing..." in one copyWith. The clear used
      // to win, so the button dimmed and nothing said why — the state the spinner now reads was
      // right, but the message beside it was silently null.
      final state = const InstructionsPanelState(signMessage: 'Signed.', saveMessage: 'Saved.')
          .copyWith(signing: true, clearMessages: true, signMessage: 'Signing...');

      expect(state.signing, isTrue);
      expect(state.signMessage, 'Signing...');
      expect(state.saveMessage, isNull, reason: 'the clear still drops what is not being set');
    });
  });

  group('saving', () {
    blocTest<InstructionsPanelBloc, InstructionsPanelState>(
      'treats a non-JSON editor as a replacement script and keeps the other known fields',
      build: () => blocFor(repository),
      act: (bloc) async {
        bloc.add(const PanelOpened());
        await Future<void>.delayed(const Duration(milliseconds: 10));
        bloc.add(const ScriptSaveRequested('#!/bin/bash\necho replaced'));
      },
      wait: const Duration(milliseconds: 20),
      verify: (_) {
        final body = repository.savedBody!;
        expect(body['script'], '#!/bin/bash\necho replaced');
        expect(body['applicationName'], 'Nextcloud');
        expect(body['platform'], 'macOS');
        // The fields a bare script does not carry have to survive the trip rather than being
        // dropped on the way through.
        expect(body['notes'], 'Researched by the AI agent.');
        expect(body['latestVersion'], '3.16.0');
        expect(body['sourceUrl'], 'https://nextcloud.com');
      },
    );

    blocTest<InstructionsPanelBloc, InstructionsPanelState>(
      'sends a whole result JSON as-is',
      build: () => blocFor(repository),
      act: (bloc) async {
        bloc.add(const PanelOpened());
        await Future<void>.delayed(const Duration(milliseconds: 10));
        bloc.add(ScriptSaveRequested(jsonEncode({
          'applicationName': 'Nextcloud',
          'platform': 'Windows',
          'method': 'Script',
          'script': '# powershell',
          'notes': 'hand written',
        })));
      },
      wait: const Duration(milliseconds: 20),
      verify: (_) {
        final body = repository.savedBody!;
        expect(body['platform'], 'Windows');
        expect(body['script'], '# powershell');
        expect(body['notes'], 'hand written');
      },
    );

    blocTest<InstructionsPanelBloc, InstructionsPanelState>(
      'unwraps the envelope an AI response comes back as',
      build: () => blocFor(repository),
      act: (bloc) async {
        bloc.add(const PanelOpened());
        await Future<void>.delayed(const Duration(milliseconds: 10));
        // So a response copied straight out of this same box can be pasted back in unchanged.
        bloc.add(ScriptSaveRequested(jsonEncode({
          'success': true,
          'results': [
            {'applicationName': 'Nextcloud', 'platform': 'Linux', 'method': 'Script', 'script': '#!/bin/sh'},
          ],
        })));
      },
      wait: const Duration(milliseconds: 20),
      verify: (_) {
        final body = repository.savedBody!;
        expect(body['platform'], 'Linux');
        expect(body['script'], '#!/bin/sh');
        expect(body.containsKey('results'), isFalse);
      },
    );

    blocTest<InstructionsPanelBloc, InstructionsPanelState>(
      'rewrites a numeric method as a name, which is the only form the server reads',
      build: () => blocFor(repository),
      act: (bloc) async {
        bloc.add(const PanelOpened());
        await Future<void>.delayed(const Duration(milliseconds: 10));
        bloc.add(ScriptSaveRequested(jsonEncode({
          'applicationName': 'Nextcloud',
          'platform': 'macOS',
          // A number here would be rejected outright: LenientEnumConverter reads UpgradeMethod
          // from a string only.
          'method': 4,
          'script': '#!/bin/bash',
        })));
      },
      wait: const Duration(milliseconds: 20),
      verify: (_) => expect(repository.savedBody!['method'], 'Script'),
    );

    blocTest<InstructionsPanelBloc, InstructionsPanelState>(
      'fills in the platform the prompt resolved when the row itself has none',
      build: () => blocFor(repository, platform: ''),
      act: (bloc) async {
        bloc.add(const PanelOpened());
        await Future<void>.delayed(const Duration(milliseconds: 10));
        bloc.add(const ScriptSaveRequested('#!/bin/bash'));
      },
      wait: const Duration(milliseconds: 20),
      verify: (_) {
        // An application nothing has been researched for has no platform on its row; the prompt
        // response reports the one research would use, and that is what every later call sends.
        expect(repository.savedBody!['platform'], 'macOS');
      },
    );
  });
}
