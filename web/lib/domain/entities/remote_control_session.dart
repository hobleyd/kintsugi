import 'dart:typed_data';

import 'package:equatable/equatable.dart';

import 'enums.dart';

/// One request to control a host, and whatever became of it. Mirrors `RemoteControlSessionDto`.
class RemoteControlSession extends Equatable {
  const RemoteControlSession({
    required this.id,
    required this.hostId,
    required this.serialNumber,
    required this.hostname,
    required this.requestedBy,
    required this.consent,
    required this.requestedAtUtc,
    required this.consentDecidedAtUtc,
    required this.startedAtUtc,
    required this.endedAtUtc,
    required this.endReason,
    required this.isActive,
  });

  final String id;
  final String? hostId;
  final String serialNumber;
  final String hostname;

  /// The administrator who asked — read off the session cookie's claims by the server, never
  /// supplied by this client. It is what the host user's own dialog names.
  final String requestedBy;

  final RemoteControlConsent consent;
  final DateTime requestedAtUtc;
  final DateTime? consentDecidedAtUtc;
  final DateTime? startedAtUtc;
  final DateTime? endedAtUtc;
  final String? endReason;

  /// True only while both sockets are joined. Distinct from a non-null [startedAtUtc], which stays
  /// true for a session that has since finished.
  final bool isActive;

  bool get isAwaitingConsent => consent == RemoteControlConsent.pending && endedAtUtc == null;

  bool get isConnectable => consent == RemoteControlConsent.granted && endedAtUtc == null;

  @override
  List<Object?> get props => [
        id,
        hostId,
        serialNumber,
        hostname,
        requestedBy,
        consent,
        requestedAtUtc,
        consentDecidedAtUtc,
        startedAtUtc,
        endedAtUtc,
        endReason,
        isActive,
      ];
}

/// Something the agent sent over a session's media socket.
///
/// The media protocol is between this client and the agent directly — the server relays it without
/// parsing any of it — so its only other description is
/// `clients/macos-agent/src/remote_protocol.rs`. Nothing in the API will catch the two drifting
/// apart, which is why the version byte is checked rather than assumed.
sealed class RemoteScreenUpdate extends Equatable {
  const RemoteScreenUpdate();
}

/// The geometry of the screen being watched. Always arrives before the first tile, and again
/// whenever the display changes.
///
/// **Two sizes, and they are not interchangeable.** The point size is the host's own coordinate
/// space and is what a click has to be converted back into; the image size is what the JPEG tiles
/// actually are, after the agent scaled them down for the link. Use the image size for a pointer
/// position and every click lands in the wrong place on a Retina display.
class RemoteDisplayGeometry extends RemoteScreenUpdate {
  const RemoteDisplayGeometry({
    required this.pointWidth,
    required this.pointHeight,
    required this.imageWidth,
    required this.imageHeight,
  });

  final double pointWidth;
  final double pointHeight;
  final int imageWidth;
  final int imageHeight;

  @override
  List<Object?> get props => [pointWidth, pointHeight, imageWidth, imageHeight];
}

/// One JPEG-encoded rectangle of the host's screen, in image pixel coordinates.
class RemoteScreenTile extends RemoteScreenUpdate {
  const RemoteScreenTile({
    required this.x,
    required this.y,
    required this.width,
    required this.height,
    required this.sequence,
    required this.jpeg,
  });

  final int x;
  final int y;
  final int width;
  final int height;

  /// Increments per tile the agent sends. Used to drop a tile that decoded out of order, since
  /// decoding is asynchronous and a stale one arriving late would repaint an old picture.
  final int sequence;

  final Uint8List jpeg;

  @override
  List<Object?> get props => [x, y, width, height, sequence, jpeg.length];
}

/// Something to do to the host. Plain values — the mapping to the wire lives in `data/`.
sealed class RemoteInput {
  const RemoteInput();
}

enum RemotePointerAction { move, down, up }

enum RemoteMouseButton { left, right, middle }

/// Coordinates are in the host's display **points**, from [RemoteDisplayGeometry].
class RemotePointerInput extends RemoteInput {
  const RemotePointerInput({
    required this.action,
    required this.x,
    required this.y,
    this.button = RemoteMouseButton.left,
  });

  final RemotePointerAction action;
  final double x;
  final double y;
  final RemoteMouseButton button;
}

class RemoteScrollInput extends RemoteInput {
  const RemoteScrollInput({
    required this.x,
    required this.y,
    required this.deltaX,
    required this.deltaY,
  });

  final double x;
  final double y;
  final double deltaX;
  final double deltaY;
}

/// A physical key, as a USB HID usage code.
///
/// The *physical* key rather than the character, because that is the only thing that can be
/// correct: a virtual keycode names a position on the keyboard and the host applies its own layout
/// to it. Send a character and an administrator on a US keyboard controlling a host set to a French
/// layout types the wrong letters.
class RemoteKeyInput extends RemoteInput {
  const RemoteKeyInput({required this.usbHidUsage, required this.isDown});

  final int usbHidUsage;
  final bool isDown;
}

/// Asks the agent for a different trade between picture quality and bandwidth.
class RemoteQualityInput extends RemoteInput {
  const RemoteQualityInput({this.jpegQuality});

  final int? jpegQuality;
}

/// A live session's media channel. Implemented in `data/` over a WebSocket.
abstract interface class RemoteControlStream {
  /// Screen updates from the agent. Closes when the session ends, however it ends.
  Stream<RemoteScreenUpdate> get updates;

  void send(RemoteInput input);

  Future<void> close();
}
