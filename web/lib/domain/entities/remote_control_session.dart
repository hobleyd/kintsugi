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
    required this.kind,
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

  /// What was asked for. The two share every piece of machinery below this point and differ only
  /// in what the agent does once the session is open — and in whether anybody at the host was
  /// asked, which is the difference that matters.
  final RemoteControlSessionKind kind;

  final RemoteControlConsent consent;
  final DateTime requestedAtUtc;
  final DateTime? consentDecidedAtUtc;
  final DateTime? startedAtUtc;
  final DateTime? endedAtUtc;
  final String? endReason;

  /// True only while both sockets are joined. Distinct from a non-null [startedAtUtc], which stays
  /// true for a session that has since finished.
  final bool isActive;

  /// Whether somebody at the host is being asked right now — **a screen session only**.
  ///
  /// A shell asks nobody, so a shell sitting on `pending` is waiting for the *agent* to answer, not
  /// for a person: it is the window between the request and the agent reporting `notRequired`.
  /// Reading the raw consent value here told the administrator a dialog was on somebody's screen
  /// when none had been raised, which is the one thing the wording around this feature must never
  /// get wrong in that direction.
  bool get isAwaitingConsent =>
      kind == RemoteControlSessionKind.screen &&
      consent == RemoteControlConsent.pending &&
      endedAtUtc == null;

  /// Whether the media socket may be opened. A shell session answers `notRequired` rather than
  /// `granted` — nobody was asked — and it opens on that answer exactly as a screen session opens
  /// on a grant, which is the same pair the server's own socket gate tests.
  bool get isConnectable =>
      (consent == RemoteControlConsent.granted || consent == RemoteControlConsent.notRequired) &&
      endedAtUtc == null;

  @override
  List<Object?> get props => [
        id,
        hostId,
        serialNumber,
        hostname,
        requestedBy,
        kind,
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
    this.canControlInput = true,
  });

  final double pointWidth;
  final double pointHeight;
  final int imageWidth;
  final int imageHeight;

  /// Whether this host will accept keyboard and mouse, or can only be watched.
  ///
  /// **Worth saying out loud, because otherwise it looks like a fault.** A view-only session shows a
  /// live picture that ignores the mouse, which reads as broken rather than restricted. It happens
  /// on Linux under Wayland, where a compositor may implement the portal's ScreenCast interface
  /// without RemoteDesktop.
  ///
  /// Defaults to true so an older agent — which sends no such field — is treated as drivable, which
  /// is what every agent before this was.
  final bool canControlInput;

  @override
  List<Object?> get props => [pointWidth, pointHeight, imageWidth, imageHeight, canControlInput];
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

/// What the terminal at the far end is. Arrives once, before the first output frame — the shell
/// analogue of [RemoteDisplayGeometry].
class RemoteShellInfo extends RemoteScreenUpdate {
  const RemoteShellInfo({required this.shell, required this.user});

  /// The program running: `/bin/zsh`, `/bin/bash`, `pwsh.exe`.
  final String shell;

  /// The account it runs as, and **the reason this is on the wire at all**: `root` on macOS and
  /// Linux, `SYSTEM` on Windows — never the logged-in user, on any platform. Shown rather than
  /// assumed, because a shell running as somebody's own account would be a materially different
  /// thing to hand out and ought to be visible as such if one ever appeared.
  final String user;

  @override
  List<Object?> get props => [shell, user];
}

/// Bytes the shell wrote to its terminal.
///
/// Deliberately not decoded to a `String` here. A frame may end mid-codepoint — the agent sends
/// whatever the PTY produced, when it produced it — so decoding per frame would corrupt any
/// character unlucky enough to straddle the boundary. The terminal emulator decodes the stream.
class RemoteShellOutput extends RemoteScreenUpdate {
  const RemoteShellOutput(this.bytes);

  final Uint8List bytes;

  @override
  List<Object?> get props => [bytes];
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

/// Bytes typed or pasted into the terminal, sent to the shell as they are.
///
/// Raw bytes rather than a string, and the only input in this protocol that travels as a binary
/// message: a terminal carries arbitrary bytes, and escaping every one of them into JSON would
/// double the traffic on the half of the connection latency is actually measured on.
class RemoteShellInput extends RemoteInput {
  const RemoteShellInput(this.bytes);

  final Uint8List bytes;
}

/// The terminal in the browser changed size; the PTY's window size follows so the shell re-wraps.
class RemoteShellResize extends RemoteInput {
  const RemoteShellResize({required this.columns, required this.rows});

  final int columns;
  final int rows;
}

/// A live session's media channel. Implemented in `data/` over a WebSocket.
abstract interface class RemoteControlStream {
  /// Screen updates from the agent. Closes when the session ends, however it ends.
  Stream<RemoteScreenUpdate> get updates;

  void send(RemoteInput input);

  Future<void> close();
}
