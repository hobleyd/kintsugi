import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/data/models/remote_control_mapper.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/remote_control_session.dart';

void main() {
  group('the tile wire format', () {
    /// Byte for byte the message `encode_tile` produces in the agent's own test
    /// (`encodes_a_tile_header_big_endian` in clients/macos-agent/src/remote_protocol.rs), with the
    /// same field values. That makes this the one place the two ends of the media protocol are
    /// checked against each other — the server relays these bytes without parsing them, so nothing
    /// in the API would ever notice the two drifting apart.
    final agentEncodedTile = Uint8List.fromList([
      1, // version
      1, // kind: JPEG tile
      0x01, 0x02, // x
      0x03, 0x04, // y
      0x05, 0x06, // width
      0x07, 0x08, // height
      0x09, 0x0A, 0x0B, 0x0C, // sequence
      0xFF, 0xD8, // the JPEG payload
    ]);

    test('reads every header field big-endian', () {
      // Read little-endian, 0x0102 becomes 0x0201 — a tile 513 pixels along instead of 258. The
      // picture still draws, just in the wrong place, which is why this is asserted field by field.
      final tile = remoteTileFromBytes(agentEncodedTile);

      expect(tile, isNotNull);
      expect(tile!.x, 0x0102);
      expect(tile.y, 0x0304);
      expect(tile.width, 0x0506);
      expect(tile.height, 0x0708);
      expect(tile.sequence, 0x090A0B0C);
      expect(tile.jpeg, [0xFF, 0xD8]);
    });

    test('agrees with the agent about how long the header is', () {
      expect(remoteTileHeaderBytes, agentEncodedTile.length - 2);
    });

    test('refuses a version it does not understand rather than drawing noise', () {
      final future = Uint8List.fromList(agentEncodedTile)..[0] = remoteProtocolVersion + 1;

      expect(remoteTileFromBytes(future), isNull);
    });

    test('refuses a message kind it does not understand', () {
      final unknownKind = Uint8List.fromList(agentEncodedTile)..[1] = 99;

      expect(remoteTileFromBytes(unknownKind), isNull);
    });

    test('refuses a message with no payload', () {
      expect(remoteTileFromBytes(Uint8List(remoteTileHeaderBytes)), isNull);
      expect(remoteTileFromBytes(Uint8List(4)), isNull);
    });

    test('refuses a zero-sized tile', () {
      final empty = Uint8List.fromList(agentEncodedTile)
        ..[6] = 0
        ..[7] = 0;

      expect(remoteTileFromBytes(empty), isNull);
    });
  });

  group('the display geometry', () {
    test('keeps the point size and the image size apart', () {
      // Conflating these is the classic remote-viewer bug: the point size is the host's own
      // coordinate space and is what a click converts back into, while the image size is only what
      // the tiles happen to be.
      final update = remoteTextUpdateFromJson(jsonEncode({
        'type': 'display',
        'pointWidth': 1512.0,
        'pointHeight': 982.0,
        'imageWidth': 1512,
        'imageHeight': 982,
      }));

      expect(update, isA<RemoteDisplayGeometry>());
      final geometry = update! as RemoteDisplayGeometry;
      expect(geometry.pointWidth, 1512.0);
      expect(geometry.imageWidth, 1512);
    });

    test('reads a scaled-down image beside a larger point size', () {
      final geometry = remoteTextUpdateFromJson(jsonEncode({
            'type': 'display',
            'pointWidth': 2560.0,
            'pointHeight': 1440.0,
            'imageWidth': 1600,
            'imageHeight': 900,
          }))! as RemoteDisplayGeometry;

      expect(geometry.pointWidth, 2560.0);
      expect(geometry.imageWidth, 1600);
    });

    test('a session is drivable unless the agent says otherwise', () {
      // An agent from before this field existed could always be driven, so an absent flag has to
      // mean true — defaulting the other way would make every older host look view-only.
      final geometry = remoteTextUpdateFromJson(jsonEncode({
            'type': 'display',
            'pointWidth': 1.0,
            'pointHeight': 2.0,
            'imageWidth': 1,
            'imageHeight': 2,
          }))! as RemoteDisplayGeometry;

      expect(geometry.canControlInput, isTrue);
    });

    test('a view-only session is reported as one', () {
      // The case this exists for: a Wayland compositor with ScreenCast but no RemoteDesktop. Without
      // the flag the viewer shows a live picture that ignores the mouse, which reads as a fault.
      final geometry = remoteTextUpdateFromJson(jsonEncode({
            'type': 'display',
            'pointWidth': 1.0,
            'pointHeight': 2.0,
            'imageWidth': 1,
            'imageHeight': 2,
            'canControlInput': false,
          }))! as RemoteDisplayGeometry;

      expect(geometry.canControlInput, isFalse);
    });

    test('drops a geometry with a zero in it', () {
      // Every pointer conversion divides by these, so a zero would be a crash per mouse move.
      expect(
        remoteTextUpdateFromJson(jsonEncode({
          'type': 'display',
          'pointWidth': 0,
          'pointHeight': 982.0,
          'imageWidth': 1512,
          'imageHeight': 982,
        })),
        isNull,
      );
    });

    test('ignores a message type it has never heard of', () {
      // A newer agent must not be able to break a session by mentioning something new.
      expect(remoteTextUpdateFromJson(jsonEncode({'type': 'clipboard', 'text': 'x'})), isNull);
    });

    test('ignores text that is not JSON', () {
      expect(remoteTextUpdateFromJson('}{'), isNull);
    });
  });

  group('input sent to the agent', () {
    test('a pointer event carries points, an action and a button', () {
      expect(
        remoteInputToJson(const RemotePointerInput(
          action: RemotePointerAction.down,
          x: 12.5,
          y: 34.25,
          button: RemoteMouseButton.right,
        )),
        {'type': 'pointer', 'action': 'down', 'x': 12.5, 'y': 34.25, 'button': 'right'},
      );
    });

    test('a move defaults to the left button, which is what the agent assumes too', () {
      expect(
        remoteInputToJson(const RemotePointerInput(action: RemotePointerAction.move, x: 1, y: 2)),
        containsPair('button', 'left'),
      );
    });

    test('a key event sends the physical key as a HID usage', () {
      // The physical key rather than the character, so the host applies its own layout — see
      // input_injection::virtual_key_for_hid. 0x00070004 is A.
      expect(
        remoteInputToJson(const RemoteKeyInput(usbHidUsage: 0x00070004, isDown: true)),
        {'type': 'key', 'hid': 0x00070004, 'down': true},
      );
    });

    test('a scroll carries both axes', () {
      expect(
        remoteInputToJson(const RemoteScrollInput(x: 1, y: 2, deltaX: 0, deltaY: -3)),
        {'type': 'scroll', 'x': 1.0, 'y': 2.0, 'deltaX': 0.0, 'deltaY': -3.0},
      );
    });

    test('a quality request with nothing set sends nothing to change', () {
      expect(remoteInputToJson(const RemoteQualityInput()), {'type': 'quality'});
    });

    test('every field name matches what the agent parses', () {
      // The agent's parse_viewer_input reads exactly these keys; a rename on this side alone is a
      // session where the mouse silently does nothing.
      final pointer = remoteInputToJson(
        const RemotePointerInput(action: RemotePointerAction.up, x: 0, y: 0),
      );
      expect(pointer!.keys, containsAll(['type', 'action', 'x', 'y', 'button']));

      final scroll = remoteInputToJson(const RemoteScrollInput(x: 0, y: 0, deltaX: 0, deltaY: 0));
      expect(scroll!.keys, containsAll(['type', 'x', 'y', 'deltaX', 'deltaY']));

      final key = remoteInputToJson(const RemoteKeyInput(usbHidUsage: 4, isDown: false));
      expect(key!.keys, containsAll(['type', 'hid', 'down']));
    });
  });

  group('the session DTO', () {
    test('reads consent sent as a name', () {
      // The C# enum carries a converter and writes its name — unlike HostStatus and friends, which
      // arrive as ordinals.
      final session = remoteControlSessionFromJson({
        'id': 'abc',
        'hostId': 'host-1',
        'serialNumber': 'C02ABC',
        'hostname': 'designer-mbp',
        'requestedBy': 'admin@example.com',
        'consent': 'Granted',
        'requestedAtUtc': '2026-09-03T10:00:00+00:00',
        'isActive': true,
      });

      expect(session.consent, RemoteControlConsent.granted);
      expect(session.hostname, 'designer-mbp');
      expect(session.requestedBy, 'admin@example.com');
      expect(session.isActive, isTrue);
      expect(session.isConnectable, isTrue);
    });

    test('reads consent sent as an ordinal, in case the converter is ever dropped', () {
      final session = remoteControlSessionFromJson({
        'id': 'abc',
        'serialNumber': 'C02ABC',
        'hostname': 'designer-mbp',
        'requestedBy': 'admin@example.com',
        'consent': 4,
        'requestedAtUtc': '2026-09-03T10:00:00+00:00',
        'isActive': false,
      });

      expect(session.consent, RemoteControlConsent.agentUnreachable);
    });

    test('an unreachable host is not connectable and is not still waiting', () {
      final session = remoteControlSessionFromJson({
        'id': 'abc',
        'serialNumber': 'C02ABC',
        'hostname': 'designer-mbp',
        'requestedBy': 'admin@example.com',
        'consent': 'AgentUnreachable',
        'requestedAtUtc': '2026-09-03T10:00:00+00:00',
        'isActive': false,
      });

      expect(session.isConnectable, isFalse);
      expect(session.isAwaitingConsent, isFalse);
    });

    test('a pending session is awaiting consent and not yet connectable', () {
      final session = remoteControlSessionFromJson({
        'id': 'abc',
        'serialNumber': 'C02ABC',
        'hostname': 'designer-mbp',
        'requestedBy': 'admin@example.com',
        'consent': 'Pending',
        'requestedAtUtc': '2026-09-03T10:00:00+00:00',
        'isActive': false,
      });

      expect(session.isAwaitingConsent, isTrue);
      expect(session.isConnectable, isFalse);
    });

    test('an ended session is neither waiting nor connectable, whatever its consent said', () {
      final session = remoteControlSessionFromJson({
        'id': 'abc',
        'serialNumber': 'C02ABC',
        'hostname': 'designer-mbp',
        'requestedBy': 'admin@example.com',
        'consent': 'Granted',
        'requestedAtUtc': '2026-09-03T10:00:00+00:00',
        'endedAtUtc': '2026-09-03T10:05:00+00:00',
        'endReason': 'the administrator disconnected',
        'isActive': false,
      });

      expect(session.isAwaitingConsent, isFalse);
      expect(session.isConnectable, isFalse);
      expect(session.endReason, 'the administrator disconnected');
    });
  });

  group('the shell half of the media protocol', () {
    // These assert the exact bytes the agents' own `frames_shell_output_behind_the_shared_two_byte_header`
    // and `decodes_shell_input_and_refuses_every_other_binary_frame` tests produce and accept. There
    // is nothing else checking that the two ends agree: the server relays these bytes without
    // parsing them, so unlike every other mapper in this directory no C# definition sits between
    // the two to make a mismatch visible.

    test('reads a shell output frame behind the shared two-byte header', () {
      final update = remoteShellOutputFromBytes(Uint8List.fromList([1, 2, 0x24, 0x20]));

      expect(update, isA<RemoteShellOutput>());
      expect(update!.bytes, [0x24, 0x20]);
    });

    test('an output frame with no payload is a frame, not a refusal', () {
      // The agent sends one when the terminal produced nothing printable, and null here means
      // "not shell output" — a different thing from "no bytes this time".
      final update = remoteShellOutputFromBytes(Uint8List.fromList([1, 2]));

      expect(update, isA<RemoteShellOutput>());
      expect(update!.bytes, isEmpty);
    });

    test('refuses a tile, a stale version, an input frame echoed back, and nothing at all', () {
      // A tile: kind 1, which must never be decoded as terminal output.
      expect(remoteShellOutputFromBytes(Uint8List.fromList([1, 1, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0xFF])), isNull);
      expect(remoteShellOutputFromBytes(Uint8List.fromList([2, 2, 0x61])), isNull);
      expect(remoteShellOutputFromBytes(Uint8List.fromList([1, 3, 0x61])), isNull);
      expect(remoteShellOutputFromBytes(Uint8List.fromList([])), isNull);
      expect(remoteShellOutputFromBytes(Uint8List.fromList([1])), isNull);
    });

    test('frames typed input the way the agent decodes it', () {
      final framed = remoteShellInputToBytes(RemoteShellInput(Uint8List.fromList([0x6C, 0x73, 0x0A])));

      // version 1, kind 3, then "ls\n" — exactly what `decode_shell_input` matches on.
      expect(framed, [1, 3, 0x6C, 0x73, 0x0A]);
    });

    test('every other input still travels as JSON', () {
      expect(remoteShellInputToBytes(const RemoteQualityInput(jpegQuality: 50)), isNull);
      expect(remoteShellInputToBytes(const RemoteShellResize(columns: 120, rows: 40)), isNull);
    });

    test('a resize names the fields the agent reads', () {
      // `parse_viewer_input` requires both and refuses a zero-sized terminal, so the names have to
      // be exactly these.
      expect(
        remoteInputToJson(const RemoteShellResize(columns: 120, rows: 40)),
        {'type': 'resize', 'cols': 120, 'rows': 40},
      );
    });

    test('typed input has no JSON form at all', () {
      // Null rather than a message: it goes out as a binary frame, and a JSON shape here would put
      // a keystroke on the wire in a form no agent reads.
      expect(remoteInputToJson(RemoteShellInput(Uint8List.fromList([0x61]))), isNull);
    });

    test('reads the shell banner the agent sends before the first output frame', () {
      final update = remoteTextUpdateFromJson('{"type":"shell","shell":"/bin/zsh","user":"david"}');

      expect(update, isA<RemoteShellInfo>());
      expect((update! as RemoteShellInfo).shell, '/bin/zsh');
      expect((update as RemoteShellInfo).user, 'david');
    });

    test('a banner missing the account is dropped rather than shown blank', () {
      // The account is the whole reason the banner exists — root on macOS and Linux, SYSTEM on
      // Windows — so a blank one would be worse than none.
      expect(remoteTextUpdateFromJson('{"type":"shell","shell":"/bin/zsh"}'), isNull);
    });
  });

  group('the session kind', () {
    test('is read from the DTO, and an older server\'s silence means a screen', () {
      Map<String, dynamic> dto(Map<String, dynamic> extra) => {
            'id': 'abc',
            'serialNumber': 'C02ABC',
            'hostname': 'designer-mbp',
            'requestedBy': 'admin@example.com',
            'consent': 'Pending',
            'requestedAtUtc': '2026-09-03T10:00:00+00:00',
            'isActive': false,
            ...extra,
          };

      expect(remoteControlSessionFromJson(dto({})).kind, RemoteControlSessionKind.screen);
      expect(remoteControlSessionFromJson(dto({'kind': 'Screen'})).kind, RemoteControlSessionKind.screen);
      expect(remoteControlSessionFromJson(dto({'kind': 'Shell'})).kind, RemoteControlSessionKind.shell);
    });

    test('a shell session is connectable on NotRequired, which is not a grant', () {
      // The server's own socket gate tests exactly this pair, and a shell session never reports
      // Granted — nobody was asked.
      final session = remoteControlSessionFromJson({
        'id': 'abc',
        'serialNumber': 'C02ABC',
        'hostname': 'web-01',
        'requestedBy': 'admin@example.com',
        'kind': 'Shell',
        'consent': 'NotRequired',
        'requestedAtUtc': '2026-09-03T10:00:00+00:00',
        'isActive': true,
      });

      expect(session.consent, RemoteControlConsent.notRequired);
      expect(session.isConnectable, isTrue);
      expect(session.isAwaitingConsent, isFalse);
    });

    test('a host that cannot provide the session is neither waiting nor connectable', () {
      final session = remoteControlSessionFromJson({
        'id': 'abc',
        'serialNumber': 'C02ABC',
        'hostname': 'web-01',
        'requestedBy': 'admin@example.com',
        'consent': 'Unavailable',
        'requestedAtUtc': '2026-09-03T10:00:00+00:00',
        'isActive': false,
      });

      expect(session.consent, RemoteControlConsent.unavailable);
      expect(session.isConnectable, isFalse);
      expect(session.isAwaitingConsent, isFalse);
    });
  });
}
