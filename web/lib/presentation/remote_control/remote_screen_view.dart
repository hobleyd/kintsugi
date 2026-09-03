import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../domain/entities/remote_control_session.dart';
import 'remote_control_bloc.dart';

/// The host's screen, and the keyboard and mouse going the other way.
///
/// Two coordinate spaces meet here and mixing them up is the classic bug in a remote viewer, so it
/// is worth naming them. The **image** space is what the tiles are in — whatever the agent scaled
/// the screen down to. The **point** space is the host's own, which is what an event has to be
/// expressed in for `CGEventPost` to put the pointer where the administrator is pointing. This
/// widget draws in image space and sends in point space, and never the other way round.
class RemoteScreenView extends StatefulWidget {
  const RemoteScreenView({
    required this.geometry,
    required this.tiles,
    required this.onInput,
    super.key,
  });

  final RemoteDisplayGeometry geometry;
  final Map<int, RemoteControlTileImage> tiles;
  final ValueChanged<RemoteInput> onInput;

  @override
  State<RemoteScreenView> createState() => _RemoteScreenViewState();
}

class _RemoteScreenViewState extends State<RemoteScreenView> {
  final FocusNode _focus = FocusNode(debugLabel: 'remote screen');

  @override
  void dispose() {
    _focus.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _buildKeyCombinationBar(context),
          const SizedBox(height: 12),
          Center(
            child: AspectRatio(
              aspectRatio: widget.geometry.imageWidth / widget.geometry.imageHeight,
              child: LayoutBuilder(
                builder: (context, constraints) => _buildScreen(constraints.biggest),
              ),
            ),
          ),
          const SizedBox(height: 8),
          Text(
            'Click the screen to give it the keyboard. Some combinations — ⌘W, ⌘Q, ⌘Tab — are '
            'claimed by this browser and cannot be forwarded; use the buttons above for those.',
            style: Theme.of(context).textTheme.bodySmall,
          ),
        ],
      );

  Widget _buildScreen(Size size) {
    // Keyboard first, then pointer, then the picture. The Focus has to be the outer of the two so a
    // click anywhere on the screen also gives it focus, which is what makes typing work without a
    // separate "click here first" step.
    return Focus(
      focusNode: _focus,
      autofocus: true,
      onKeyEvent: _onKeyEvent,
      child: Listener(
        onPointerDown: (event) {
          // Explicitly, because a Listener does not take focus on its own and a remote screen that
          // ignores the keyboard until something else is clicked is baffling.
          _focus.requestFocus();
          _sendPointer(RemotePointerAction.down, event.localPosition, size, event.buttons);
        },
        onPointerMove: (event) => _sendPointer(RemotePointerAction.move, event.localPosition, size, event.buttons),
        onPointerHover: (event) => _sendPointer(RemotePointerAction.move, event.localPosition, size, 0),
        onPointerUp: (event) => _sendPointer(RemotePointerAction.up, event.localPosition, size, event.buttons),
        onPointerSignal: _onPointerSignal,
        child: MouseRegion(
          // The host's own cursor is in the picture (the agent captures it), so a second one drawn
          // by this browser on top would be two pointers moving together — confusing, and it hides
          // the one that matters.
          cursor: SystemMouseCursors.none,
          child: CustomPaint(
            painter: _RemoteScreenPainter(tiles: widget.tiles, geometry: widget.geometry),
            child: const SizedBox.expand(),
          ),
        ),
      ),
    );
  }

  Widget _buildKeyCombinationBar(BuildContext context) => Wrap(
        spacing: 8,
        runSpacing: 8,
        children: [
          for (final entry in RemoteKeyCombinations.combinations.entries)
            OutlinedButton(
              onPressed: () {
                for (final input in RemoteKeyCombinations.press(entry.value)) {
                  widget.onInput(input);
                }
              },
              child: Text(entry.key),
            ),
        ],
      );

  void _sendPointer(RemotePointerAction action, Offset local, Size size, int buttons) {
    if (size.width <= 0 || size.height <= 0) return;

    // Clamped rather than dropped: a drag that leaves the picture should hold at the edge, which is
    // what dragging a window against the side of a screen does locally. Dropping the event instead
    // strands the host mid-drag with the button still down.
    final x = (local.dx / size.width).clamp(0.0, 1.0) * widget.geometry.pointWidth;
    final y = (local.dy / size.height).clamp(0.0, 1.0) * widget.geometry.pointHeight;

    widget.onInput(RemotePointerInput(action: action, x: x, y: y, button: _buttonFrom(buttons)));
  }

  void _onPointerSignal(PointerSignalEvent event) {
    if (event is! PointerScrollEvent) return;

    final size = context.size;
    if (size == null || size.width <= 0 || size.height <= 0) return;

    final x = (event.localPosition.dx / size.width).clamp(0.0, 1.0) * widget.geometry.pointWidth;
    final y = (event.localPosition.dy / size.height).clamp(0.0, 1.0) * widget.geometry.pointHeight;

    widget.onInput(RemoteScrollInput(
      x: x,
      y: y,
      deltaX: event.scrollDelta.dx,
      deltaY: event.scrollDelta.dy,
    ));
  }

  KeyEventResult _onKeyEvent(FocusNode node, KeyEvent event) {
    // The *physical* key, never the character: a virtual keycode names a position on the keyboard
    // and the host applies its own layout to it. See `input_injection::virtual_key_for_hid`.
    final usage = event.physicalKey.usbHidUsage;

    switch (event) {
      case KeyDownEvent():
      // A repeat is another press as far as the host is concerned, which is what makes holding a
      // key down work.
      case KeyRepeatEvent():
        widget.onInput(RemoteKeyInput(usbHidUsage: usage, isDown: true));
      case KeyUpEvent():
        widget.onInput(RemoteKeyInput(usbHidUsage: usage, isDown: false));
      default:
        return KeyEventResult.ignored;
    }

    // Handled, always, so Tab does not move focus out of the screen and the app's own shortcuts do
    // not fire while somebody is typing into another machine. It does not stop the *browser*
    // claiming its own combinations — see the note on RemoteKeyCombinations.
    return KeyEventResult.handled;
  }

  /// `PointerEvent.buttons` is a mask; the primary is reported as bit 0.
  static RemoteMouseButton _buttonFrom(int buttons) {
    if (buttons & kSecondaryMouseButton != 0) return RemoteMouseButton.right;
    if (buttons & kMiddleMouseButton != 0) return RemoteMouseButton.middle;
    return RemoteMouseButton.left;
  }
}

/// The key combinations a browser will not let a page have, offered as buttons instead.
///
/// **A permanent limitation rather than a gap to be closed.** A web page cannot intercept ⌘W, ⌘Q,
/// ⌘T or ⌘Tab — the browser and the operating system claim them before any handler runs — so a
/// session driven from a browser can never forward them as keystrokes. Sending them as an explicit
/// sequence is the only way, and it is what every browser-based remote desktop does.
abstract final class RemoteKeyCombinations {
  /// USB HID usages, as `PhysicalKeyboardKey.usbHidUsage` reports them.
  static const _leftGui = 0x000700E3;
  static const _leftAlt = 0x000700E2;
  static const _escape = 0x00070029;
  static const _keyW = 0x0007001A;
  static const _keyQ = 0x00070014;
  static const _space = 0x0007002C;

  static const combinations = <String, List<int>>{
    // The most useful one in a support session by a wide margin, and completely unreachable by
    // keystroke from a browser.
    'Force Quit ⌘⌥⎋': [_leftGui, _leftAlt, _escape],
    'Spotlight ⌘Space': [_leftGui, _space],
    'Close Window ⌘W': [_leftGui, _keyW],
    'Quit App ⌘Q': [_leftGui, _keyQ],
  };

  /// Presses the keys in order and releases them in reverse, which is what a hand does and what
  /// anything watching for a chord expects.
  static List<RemoteInput> press(List<int> usages) => [
        for (final usage in usages) RemoteKeyInput(usbHidUsage: usage, isDown: true),
        for (final usage in usages.reversed) RemoteKeyInput(usbHidUsage: usage, isDown: false),
      ];
}

class _RemoteScreenPainter extends CustomPainter {
  const _RemoteScreenPainter({required this.tiles, required this.geometry});

  final Map<int, RemoteControlTileImage> tiles;
  final RemoteDisplayGeometry geometry;

  @override
  void paint(Canvas canvas, Size size) {
    if (geometry.imageWidth <= 0 || geometry.imageHeight <= 0) return;

    final scaleX = size.width / geometry.imageWidth;
    final scaleY = size.height / geometry.imageHeight;

    // A dark ground under the tiles, so the gaps before the first full frame arrives read as "not
    // painted yet" rather than as whatever was behind the widget.
    canvas.drawRect(Offset.zero & size, Paint()..color = const Color(0xFF101014));

    // filterQuality matters here: the tiles are almost always being scaled to fit, and the default
    // nearest-neighbour makes remote text unreadable in a way that looks like a bad JPEG quality
    // setting rather than a sampling choice.
    final paint = Paint()..filterQuality = FilterQuality.medium;

    for (final tile in tiles.values) {
      canvas.drawImageRect(
        tile.image,
        Rect.fromLTWH(0, 0, tile.image.width.toDouble(), tile.image.height.toDouble()),
        Rect.fromLTWH(tile.x * scaleX, tile.y * scaleY, tile.width * scaleX, tile.height * scaleY),
        paint,
      );
    }
  }

  @override
  bool shouldRepaint(_RemoteScreenPainter oldDelegate) =>
      !identical(oldDelegate.tiles, tiles) || oldDelegate.geometry != geometry;
}
