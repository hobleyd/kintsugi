import 'dart:convert';
import 'dart:typed_data';

import '../../core/network/json_reader.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/remote_control_session.dart';

/// Maps `RemoteControlSessionDto`.
RemoteControlSession remoteControlSessionFromJson(Map<String, dynamic> json) => RemoteControlSession(
      id: json['id'].toString(),
      hostId: json['hostId']?.toString(),
      serialNumber: json['serialNumber']?.toString() ?? '',
      hostname: json['hostname']?.toString() ?? '',
      requestedBy: json['requestedBy']?.toString() ?? '',
      consent: enumFromJson(
        json['consent'],
        RemoteControlConsent.values,
        const ['Pending', 'Granted', 'Denied', 'TimedOut', 'AgentUnreachable'],
        RemoteControlConsent.pending,
      ),
      requestedAtUtc: dateTimeRequiredFromJson(json['requestedAtUtc']),
      consentDecidedAtUtc: dateTimeFromJson(json['consentDecidedAtUtc']),
      startedAtUtc: dateTimeFromJson(json['startedAtUtc']),
      endedAtUtc: dateTimeFromJson(json['endedAtUtc']),
      endReason: json['endReason']?.toString(),
      isActive: json['isActive'] == true,
    );

// =================================================================================================
// The media protocol.
//
// This half is not a DTO mapping at all: it is the other end of
// `clients/macos-agent/src/remote_protocol.rs`, and the API only relays it. So unlike every other
// mapper here, nothing on the server would notice the two drifting apart — which is why the version
// byte is checked and a tile that does not fit its own header is dropped rather than trusted.
// =================================================================================================

/// Bumped only for a change the other end cannot ignore.
const int remoteProtocolVersion = 1;

/// `version, kind, x, y, width, height, sequence`.
const int remoteTileHeaderBytes = 14;

const int _kindJpegTile = 1;

/// Reads a text message from the agent — currently only the display geometry.
///
/// Returns null for anything unrecognised, so a newer agent sending something this build has never
/// heard of does not take a session down.
RemoteScreenUpdate? remoteTextUpdateFromJson(String text) {
  final Object? decoded;
  try {
    decoded = jsonDecode(text);
  } on FormatException {
    return null;
  }

  if (decoded is! Map<String, dynamic>) return null;

  switch (decoded['type']) {
    case 'display':
      final pointWidth = _asDouble(decoded['pointWidth']);
      final pointHeight = _asDouble(decoded['pointHeight']);
      final imageWidth = _asInt(decoded['imageWidth']);
      final imageHeight = _asInt(decoded['imageHeight']);

      // A zero in any of these would make every pointer conversion a division by zero, so the
      // message is dropped rather than half-applied.
      if (pointWidth <= 0 || pointHeight <= 0 || imageWidth <= 0 || imageHeight <= 0) {
        return null;
      }

      return RemoteDisplayGeometry(
        pointWidth: pointWidth,
        pointHeight: pointHeight,
        imageWidth: imageWidth,
        imageHeight: imageHeight,
        // Absent means true: an agent from before this field existed could always be driven, and
        // defaulting the other way would make every older host look view-only.
        canControlInput: decoded['canControlInput'] != false,
      );

    default:
      return null;
  }
}

/// Reads one binary message from the agent.
///
/// Big-endian, matching `encode_tile` on the agent side — which chose big-endian precisely because
/// it is `ByteData`'s default here, so the reader does not have to remember to ask.
RemoteScreenTile? remoteTileFromBytes(Uint8List message) {
  if (message.lengthInBytes <= remoteTileHeaderBytes) return null;

  final header = ByteData.sublistView(message, 0, remoteTileHeaderBytes);

  if (header.getUint8(0) != remoteProtocolVersion) return null;
  if (header.getUint8(1) != _kindJpegTile) return null;

  final width = header.getUint16(6);
  final height = header.getUint16(8);
  if (width == 0 || height == 0) return null;

  return RemoteScreenTile(
    x: header.getUint16(2),
    y: header.getUint16(4),
    width: width,
    height: height,
    sequence: header.getUint32(10),
    jpeg: Uint8List.sublistView(message, remoteTileHeaderBytes),
  );
}

/// Maps an input event to what the agent's `parse_viewer_input` reads.
Map<String, Object?> remoteInputToJson(RemoteInput input) => switch (input) {
      RemotePointerInput(:final action, :final x, :final y, :final button) => {
          'type': 'pointer',
          'action': switch (action) {
            RemotePointerAction.move => 'move',
            RemotePointerAction.down => 'down',
            RemotePointerAction.up => 'up',
          },
          'x': x,
          'y': y,
          'button': switch (button) {
            RemoteMouseButton.left => 'left',
            RemoteMouseButton.right => 'right',
            RemoteMouseButton.middle => 'middle',
          },
        },
      RemoteScrollInput(:final x, :final y, :final deltaX, :final deltaY) => {
          'type': 'scroll',
          'x': x,
          'y': y,
          'deltaX': deltaX,
          'deltaY': deltaY,
        },
      RemoteKeyInput(:final usbHidUsage, :final isDown) => {
          'type': 'key',
          'hid': usbHidUsage,
          'down': isDown,
        },
      RemoteQualityInput(:final jpegQuality) => {
          'type': 'quality',
          // A null-aware element: omitted entirely when unset, which is what the agent's
          // `parse_viewer_input` reads as "leave this as it is".
          'jpegQuality': ?jpegQuality,
        },
    };

double _asDouble(Object? raw) => raw is num ? raw.toDouble() : 0;

int _asInt(Object? raw) => raw is num ? raw.toInt() : 0;
