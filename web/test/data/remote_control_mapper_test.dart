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
      expect(pointer.keys, containsAll(['type', 'action', 'x', 'y', 'button']));

      final scroll = remoteInputToJson(const RemoteScrollInput(x: 0, y: 0, deltaX: 0, deltaY: 0));
      expect(scroll.keys, containsAll(['type', 'x', 'y', 'deltaX', 'deltaY']));

      final key = remoteInputToJson(const RemoteKeyInput(usbHidUsage: 4, isDown: false));
      expect(key.keys, containsAll(['type', 'hid', 'down']));
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
}
