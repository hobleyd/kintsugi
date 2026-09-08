import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:xterm/xterm.dart';

import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../domain/entities/remote_control_session.dart';

/// The terminal itself: a real VT emulator fed by the agent's output frames, typing back into them.
///
/// # Why a terminal emulator rather than a text box
///
/// What arrives is not lines of text. It is whatever the shell wrote to its PTY — cursor moves,
/// colour, scroll regions, alternate-screen switches, everything `top`, `vim`, `less` and a
/// progress bar produce. Rendering that as text shows the escape sequences instead of obeying
/// them, which makes exactly the programs an administrator opens a terminal for unusable. `xterm`
/// is a pure-Dart emulator, so it works on web where nothing platform-specific would.
///
/// # The two things this widget owns that the BLoC deliberately does not
///
/// **The scrollback**, because a terminal is a mutable buffer fed imperatively, and putting it in
/// an `Equatable` state would mean re-emitting a growing buffer on every keystroke — see
/// `RemoteControlBloc.shellOutput`.
///
/// **The UTF-8 decoder**, and it has to be one decoder for the whole session rather than one per
/// frame. The agent sends bytes when it has them, so a frame can end in the middle of a multi-byte
/// character; decoding each frame on its own turns any such character into a replacement mark. A
/// single `Utf8Decoder` in non-terminating mode holds the partial sequence over to the next frame,
/// which is the whole reason [RemoteShellOutput] carries bytes rather than a string.
class RemoteShellView extends StatefulWidget {
  const RemoteShellView({
    required this.output,
    required this.onInput,
    super.key,
  });

  /// The agent's terminal output. Subscribed to once, for the life of this widget.
  final Stream<Uint8List> output;

  final ValueChanged<RemoteInput> onInput;

  @override
  State<RemoteShellView> createState() => _RemoteShellViewState();
}

class _RemoteShellViewState extends State<RemoteShellView> {
  /// Scrollback deep enough that the output of a build or a long `journalctl` is still there to
  /// scroll back through, which is most of why anybody reads a terminal after the fact.
  static const int _scrollbackLines = 5000;

  late final Terminal _terminal = Terminal(maxLines: _scrollbackLines);
  final TerminalController _controller = TerminalController();

  /// Supplied rather than left to xterm to create, because [Listener] above needs something to
  /// focus. Disposed here for the same reason: a node this widget made is a node this widget owns.
  final FocusNode _focusNode = FocusNode(debugLabel: 'remote terminal');

  StreamSubscription<Uint8List>? _subscription;

  /// Takes the keyboard on a press, whatever that press later turns out to be.
  ///
  /// Two things conspire to lose it otherwise, and together they are why clicking back into a
  /// terminal took a random number of attempts.
  ///
  /// xterm focuses from `onTapDown`, so it needs the press to be recognised as a *tap*. A press
  /// that drifts a pixel or two — which is most real clicks — is a drag instead, and drives its
  /// selection; nothing focuses. Worse, that drag leaves a selection behind, and xterm's next
  /// `onTapDown` is spent clearing it rather than focusing, so the click after a drifting one is
  /// wasted too. Listening for the raw pointer event side-steps all of it: a [Listener] does not
  /// compete in the gesture arena, so it fires on every press however the press is later
  /// interpreted, and xterm's own tap, drag-to-select and scroll handling are untouched.
  ///
  /// The microtask is the other half, and without it this does nothing at all when it is most
  /// needed. A focused text field anywhere on the page unfocuses itself on any pointer down
  /// outside it (`TapRegion`, via `onTapOutside`), and that runs *after* this handler — so a
  /// request made inline is immediately undone and focus lands on the route's modal scope, with
  /// the keyboard going nowhere. Deferring to the end of the current event dispatch puts this
  /// last, which is the only position that survives.
  void _takeKeyboard() => scheduleMicrotask(() {
        if (mounted) _focusNode.requestFocus();
      });

  /// One decoder for the whole session. See the class note — per-frame decoding corrupts any
  /// character that straddles a frame boundary.
  final Converter<List<int>, String> _decoder = const Utf8Decoder(allowMalformed: true);

  /// The last size reported to the host, so an identical resize is not sent again. `onResize` fires
  /// on every layout pass, not only on a change.
  int _columns = 0;
  int _rows = 0;

  @override
  void initState() {
    super.initState();

    // Typing. `onOutput` is the emulator's own name for "the user produced these characters",
    // already encoded the way a terminal expects — arrow keys as escape sequences, Ctrl-C as 0x03.
    // Sent as UTF-8 bytes, which is what the PTY at the other end is written with.
    _terminal.onOutput = (text) =>
        widget.onInput(RemoteShellInput(Uint8List.fromList(const Utf8Encoder().convert(text))));

    // The host re-wraps to whatever the browser is showing, so a window dragged wider is not a
    // terminal still wrapping at 80 columns.
    _terminal.onResize = (width, height, _, _) {
      if (width == _columns && height == _rows) return;
      _columns = width;
      _rows = height;
      widget.onInput(RemoteShellResize(columns: width, rows: height));
    };

    _subscription = widget.output.listen((bytes) => _terminal.write(_decoder.convert(bytes)));
  }

  @override
  void dispose() {
    _focusNode.dispose();
    unawaited(_subscription?.cancel());
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return Container(
      // A fixed height rather than an unbounded one: this sits inside the page's vertical scroll
      // view, which gives its children infinite height — a terminal laid out in that resolves to
      // one row and reports it to the host, so the shell wraps at a single line.
      height: 560,
      decoration: BoxDecoration(
        color: palette.background,
        borderRadius: BorderRadius.circular(4),
        border: Border.all(color: palette.border),
      ),
      padding: const EdgeInsets.all(8),
      child: Listener(
        onPointerDown: (_) => _takeKeyboard(),
        // **Space, and space alone, has to be taken back from the framework — on web this is the
        // difference between a terminal that types spaces and one that does not.**
        //
        // A printable character does not reach xterm as a key event at all: the emulator attaches a
        // `TextInput` connection and reads what the browser's own hidden input element receives,
        // which is why every other character works with no help from here. But Flutter web asks the
        // framework whether it consumed each key press and calls `preventDefault()` on the browser
        // event when the answer is yes — and `WidgetsApp`'s web shortcuts map a bare space to
        // `PrioritizedIntents([ActivateIntent, ScrollIntent(page down)])`. The scroll half is always
        // enabled, because there is a `Scrollable` above this widget (the page's, and xterm's own),
        // so every space was claimed, prevented, and never typed: the input element saw nothing and
        // the page nudged down instead. Nothing else was affected, which is what makes it read as a
        // terminal fault rather than as a shortcut.
        //
        // `DoNothingAndStopPropagationIntent` is the answer, and both halves of its name are
        // load-bearing: its action reports the key *unhandled*, so the embedding lets the browser
        // insert the character, and it stops the event climbing any higher, so `WidgetsApp`'s
        // mapping never runs. It is what a `TextField` gets for the same collision by being an
        // `EditableText`; xterm's text input is not one, so it inherits none of that.
        //
        // No other key needs this. Every key xterm cares about is answered by `Terminal.keyInput`,
        // which writes the escape sequence itself and reports the press handled — the
        // `preventDefault` that follows is then right, because the character has already been sent.
        child: Shortcuts(
          shortcuts: const <ShortcutActivator, Intent>{
            SingleActivator(LogicalKeyboardKey.space): DoNothingAndStopPropagationIntent(),
          },
          child: TerminalView(
            _terminal,
            controller: _controller,
            focusNode: _focusNode,
            // The terminal takes the keyboard as soon as the session opens, so an administrator can
            // type straight away rather than having to click into it first.
            autofocus: true,
            backgroundOpacity: 0,
            theme: _themeFor(palette),
            // Asked for by the family `google_fonts` registers, not by the human name of the face —
            // see AppTheme.monoFamily, which is where that distinction and its consequence are
            // written down. Naming `'Share Tech Mono'` here matched nothing, and the terminal
            // rendered in the proportional default. xterm's own fallback list is left alone below
            // it, which costs nothing on web and is what a desktop build would land on.
            textStyle: TerminalStyle(fontFamily: AppTheme.monoFamily, fontSize: 13),
          ),
        ),
      ),
    );
  }

  /// The emulator's own 16-colour palette.
  ///
  /// Left as the standard xterm colours rather than themed to match the app: a shell's own prompt,
  /// `ls --color` and every TUI choose from these by *index*, on the understanding that index 1 is
  /// red and index 2 is green. Re-mapping them to a house palette would make a failing build print
  /// its errors in whatever colour happened to sit at index 1 — so only the ground and the
  /// foreground follow the theme, which is what a terminal application expects to vary.
  TerminalTheme _themeFor(KintsugiPalette palette) => TerminalTheme(
        cursor: palette.neon,
        selection: palette.neon.withValues(alpha: 0.35),
        foreground: palette.text,
        background: palette.background,
        black: const Color(0xFF000000),
        red: const Color(0xFFCD3131),
        green: const Color(0xFF0DBC79),
        yellow: const Color(0xFFE5E510),
        blue: const Color(0xFF2472C8),
        magenta: const Color(0xFFBC3FBC),
        cyan: const Color(0xFF11A8CD),
        white: const Color(0xFFE5E5E5),
        brightBlack: const Color(0xFF666666),
        brightRed: const Color(0xFFF14C4C),
        brightGreen: const Color(0xFF23D18B),
        brightYellow: const Color(0xFFF5F543),
        brightBlue: const Color(0xFF3B8EEA),
        brightMagenta: const Color(0xFFD670D6),
        brightCyan: const Color(0xFF29B8DB),
        brightWhite: const Color(0xFFFFFFFF),
        searchHitBackground: palette.neon,
        searchHitBackgroundCurrent: palette.neon,
        searchHitForeground: palette.background,
      );
}
