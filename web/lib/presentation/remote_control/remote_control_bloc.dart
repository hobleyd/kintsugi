import 'dart:async';
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/bloc/polling.dart';
import '../../core/network/api_exception.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/remote_control_session.dart';
import '../../domain/usecases/remote_control_usecases.dart';

sealed class RemoteControlEvent extends Equatable {
  const RemoteControlEvent();

  @override
  List<Object?> get props => const [];
}

/// Pressing Connect or Terminal: opens a session of that kind on the host.
///
/// A screen session puts a consent dialog in front of whoever is sitting at the host and waits for
/// it. A shell session asks nobody — see `RemoteControlSessionKind` on the server for why, and what
/// stands in for the dialog.
final class RemoteControlRequested extends RemoteControlEvent {
  const RemoteControlRequested(this.hostId, this.kind);

  final String hostId;
  final RemoteControlSessionKind kind;

  @override
  List<Object?> get props => [hostId, kind];
}

/// The poll that watches for the host user's answer, and afterwards for the session ending.
final class RemoteControlSessionPolled extends RemoteControlEvent {
  const RemoteControlSessionPolled();
}

final class RemoteControlDisconnectRequested extends RemoteControlEvent {
  const RemoteControlDisconnectRequested();
}

/// One decoded tile, ready to paint. Raised by the bloc's own stream subscription.
final class RemoteControlTileDecoded extends RemoteControlEvent {
  const RemoteControlTileDecoded(this.key, this.tile);

  final int key;
  final RemoteControlTileImage tile;

  @override
  List<Object?> get props => [key, tile];
}

/// The terminal's banner — which shell, and whose account. Arrives once, before any output.
final class RemoteControlShellStarted extends RemoteControlEvent {
  const RemoteControlShellStarted(this.shell);

  final RemoteShellInfo shell;

  @override
  List<Object?> get props => [shell];
}

final class RemoteControlGeometryChanged extends RemoteControlEvent {
  const RemoteControlGeometryChanged(this.geometry);

  final RemoteDisplayGeometry geometry;

  @override
  List<Object?> get props => [geometry];
}

final class RemoteControlStreamClosed extends RemoteControlEvent {
  const RemoteControlStreamClosed();
}

final class RemoteControlInputSent extends RemoteControlEvent {
  const RemoteControlInputSent(this.input);

  final RemoteInput input;

  @override
  List<Object?> get props => const [];
}

/// A tile that has been decoded and is currently on screen.
class RemoteControlTileImage extends Equatable {
  const RemoteControlTileImage({
    required this.image,
    required this.x,
    required this.y,
    required this.width,
    required this.height,
    required this.sequence,
  });

  final ui.Image image;
  final int x;
  final int y;
  final int width;
  final int height;
  final int sequence;

  @override
  List<Object?> get props => [x, y, width, height, sequence];
}

final class RemoteControlState extends Equatable {
  const RemoteControlState({
    this.kind = RemoteControlSessionKind.screen,
    this.session,
    this.geometry,
    this.shell,
    this.tiles = const {},
    this.connecting = false,
    this.error,
  });

  /// What was asked for, known from the moment the request is made rather than only once the
  /// session comes back. That matters for exactly one thing: what [status] says while the request
  /// is in flight, which is the one point at which there is no session to read the kind off.
  final RemoteControlSessionKind kind;

  final RemoteControlSession? session;
  final RemoteDisplayGeometry? geometry;

  /// What the terminal at the far end is, on a shell session. Its arrival is what says the shell is
  /// live — the same role [geometry] plays for a screen session. The output itself is *not* here:
  /// see [RemoteControlBloc.shellOutput].
  final RemoteShellInfo? shell;

  /// Decoded tiles, keyed by their top-left corner — except the full frame, which sits under
  /// [RemoteControlState.fullFrameKey] (see there for why).
  ///
  /// Kept as a map of live tiles rather than composited into one offscreen image, which is what
  /// lets a repaint be a handful of `drawImageRect` calls with no surface to allocate or read back.
  /// The map is bounded by the tile grid — a few dozen entries — because a new tile for a position
  /// replaces the one there.
  final Map<int, RemoteControlTileImage> tiles;

  /// Where the full frame lives in [tiles].
  ///
  /// Not `_tileKey(0, 0)`, deliberately. The agent sends a whole-image tile at (0, 0) and later
  /// 256px tiles for whatever changed — and the top-left of those is *also* at (0, 0). Keyed by
  /// position alone, the first cursor pass through that corner replaced the full frame with one
  /// 256px square and disposed the rest of the picture, leaving the dark ground everywhere the host
  /// had not changed since. Negative, because a position key packs two `u16`s and is never
  /// negative.
  static const int fullFrameKey = -1;

  final bool connecting;
  final String? error;

  bool get isAwaitingConsent => session?.isAwaitingConsent ?? false;

  bool get isStreaming => (geometry != null || shell != null) && (session?.endedAtUtc == null);

  /// What the screen says while there is no picture. Null once there is one.
  String? get status {
    if (error != null) return error;

    // Nobody is asked for a shell — see RemoteControlSessionKind.shell — so 'Asking…' would
    // describe a dialog that is never raised, on the one kind of session whose whole justification
    // is that the administrator knows nobody was consulted.
    if (connecting) {
      return kind == RemoteControlSessionKind.shell ? 'Connecting…' : 'Asking…';
    }

    final session = this.session;
    if (session == null) return null;

    if (session.endedAtUtc != null) {
      return 'The session ended: ${session.endReason ?? 'the connection closed'}.';
    }

    // Granted (a screen session) and NotRequired (a shell) are the two answers that open a
    // session, so both mean "connecting" until the far end has said what it is.
    return switch (session.consent) {
      RemoteControlConsent.granted => geometry == null ? 'Connecting to ${session.hostname}…' : null,
      RemoteControlConsent.notRequired =>
        shell == null ? 'Opening a terminal on ${session.hostname}…' : null,
      // A shell row is created Pending and the agent answers NotRequired a moment later, so this is
      // the wait for the *agent*, not for a person: the consent label's "waiting for the person at
      // the keyboard" belongs to a screen session alone.
      RemoteControlConsent.pending when session.kind == RemoteControlSessionKind.shell =>
        'Connecting to ${session.hostname}…',
      final consent => consent.label,
    };
  }

  RemoteControlState copyWith({
    RemoteControlSessionKind? kind,
    RemoteControlSession? session,
    RemoteDisplayGeometry? geometry,
    RemoteShellInfo? shell,
    Map<int, RemoteControlTileImage>? tiles,
    bool? connecting,
    String? error,
    bool clearError = false,
  }) =>
      RemoteControlState(
        kind: kind ?? this.kind,
        session: session ?? this.session,
        geometry: geometry ?? this.geometry,
        shell: shell ?? this.shell,
        tiles: tiles ?? this.tiles,
        connecting: connecting ?? this.connecting,
        error: clearError ? null : (error ?? this.error),
      );

  @override
  List<Object?> get props => [kind, session, geometry, shell, tiles, connecting, error];
}

/// Drives one remote control session from Connect to hang-up.
///
/// Two channels, because the session has two halves. The consent handshake and the session's
/// outcome are ordinary REST, **polled** — the same mechanism every other screen here uses, and the
/// right one: a person deciding whether to allow this takes seconds, and a socket held open waiting
/// for them would be a socket held open for a session that may never happen. Only once consent is
/// granted does a socket open, and it carries nothing but pixels and input.
class RemoteControlBloc extends Bloc<RemoteControlEvent, RemoteControlState>
    with Polling<RemoteControlEvent, RemoteControlState> {
  RemoteControlBloc({
    required RequestRemoteControlSession requestSession,
    required GetRemoteControlSession getSession,
    required EndRemoteControlSession endSession,
    required OpenRemoteControlStream openStream,
  })  : _requestSession = requestSession,
        _getSession = getSession,
        _endSession = endSession,
        _openStream = openStream,
        super(const RemoteControlState()) {
    on<RemoteControlRequested>(_onRequested);
    on<RemoteControlSessionPolled>(_onPolled);
    on<RemoteControlDisconnectRequested>(_onDisconnectRequested);
    on<RemoteControlGeometryChanged>(_onGeometryChanged);
    on<RemoteControlShellStarted>(_onShellStarted);
    on<RemoteControlTileDecoded>(_onTileDecoded);
    on<RemoteControlStreamClosed>(_onStreamClosed);
    on<RemoteControlInputSent>(_onInputSent);
  }

  final RequestRemoteControlSession _requestSession;
  final GetRemoteControlSession _getSession;
  final EndRemoteControlSession _endSession;
  final OpenRemoteControlStream _openStream;

  RemoteControlStream? _stream;
  StreamSubscription<RemoteScreenUpdate>? _subscription;

  /// Terminal output, as a stream rather than as state.
  ///
  /// Deliberately not in [RemoteControlState]. A terminal emulator keeps its own scrollback and is
  /// fed imperatively, so putting the bytes in state would mean either re-emitting an
  /// ever-growing buffer on every keystroke or emitting a value whose equality is meaningless —
  /// and this state is `Equatable` precisely so that a poll finding nothing new rebuilds nothing.
  /// Broadcast, so a widget rebuilt by a state change can resubscribe.
  final StreamController<Uint8List> _shellOutput = StreamController<Uint8List>.broadcast();

  Stream<Uint8List> get shellOutput => _shellOutput.stream;

  /// The newest sequence number seen per tile position.
  ///
  /// JPEG decoding is asynchronous, so two tiles for the same position can finish out of order and
  /// the older one would repaint stale pixels over newer ones — a smear that stays until something
  /// on the host changes that region again.
  final Map<int, int> _newestSequence = {};

  /// The sequence number of the full frame on screen. A partial tile encoded *before* it describes
  /// pixels the full frame has already superseded, so it is dropped rather than painted on top.
  int _fullFrameSequence = -1;

  Future<void> _onRequested(RemoteControlRequested event, Emitter<RemoteControlState> emit) async {
    emit(state.copyWith(kind: event.kind, connecting: true, clearError: true));

    try {
      final session = await _requestSession(event.hostId, event.kind);
      emit(state.copyWith(session: session, connecting: false));

      // Two seconds: the answer comes from a human clicking a dialog, so this is about being
      // responsive to them rather than about server load. It keeps running after consent, because
      // it is also how the screen notices the session ending from the host's side.
      startPolling(const Duration(seconds: 2), const RemoteControlSessionPolled());

      if (session.isConnectable) _attachStream(session.id);
    } on ApiException catch (error) {
      emit(state.copyWith(connecting: false, error: error.message));
    }
  }

  Future<void> _onPolled(RemoteControlSessionPolled event, Emitter<RemoteControlState> emit) async {
    final current = state.session;
    if (current == null) return;

    try {
      final session = await _getSession(current.id);
      if (session == null) return;

      emit(state.copyWith(session: session));

      if (session.isConnectable) {
        _attachStream(session.id);
      } else if (session.endedAtUtc != null) {
        stopPolling();
        await _detachStream();
      }
    } on ApiException catch (error) {
      // A failed poll leaves the session alone: a session that is streaming perfectly well must
      // not be torn down because one status call missed.
      emit(state.copyWith(error: error.message));
    }
  }

  Future<void> _onDisconnectRequested(
    RemoteControlDisconnectRequested event,
    Emitter<RemoteControlState> emit,
  ) async {
    final session = state.session;
    stopPolling();
    await _detachStream();

    if (session == null) return;

    try {
      // Told to the server rather than only closing the socket, because this is what stops the
      // agent capturing. A socket closing looks the same to the agent whether the administrator
      // hung up or their network dropped, and the second must not silently end a session.
      await _endSession(session.id);
    } on ApiException catch (error) {
      emit(state.copyWith(error: error.message));
    }

    add(const RemoteControlSessionPolled());
  }

  void _onShellStarted(RemoteControlShellStarted event, Emitter<RemoteControlState> emit) {
    emit(state.copyWith(shell: event.shell));
  }

  void _onGeometryChanged(RemoteControlGeometryChanged event, Emitter<RemoteControlState> emit) {
    // Every tile position is meaningless under new geometry, so they go. The agent sends a full
    // frame straight after a geometry change for exactly this reason.
    _newestSequence.clear();
    _fullFrameSequence = -1;
    for (final stale in state.tiles.values) {
      stale.image.dispose();
    }
    emit(state.copyWith(geometry: event.geometry, tiles: const {}));
  }

  void _onTileDecoded(RemoteControlTileDecoded event, Emitter<RemoteControlState> emit) {
    // A tile covering the whole image is a full frame; everything else on screen is now stale, and
    // keeping it would leave the old picture showing through wherever the new one is smaller.
    final geometry = state.geometry;
    final isFullFrame = geometry != null &&
        event.tile.x == 0 &&
        event.tile.y == 0 &&
        event.tile.width >= geometry.imageWidth &&
        event.tile.height >= geometry.imageHeight;

    final key = isFullFrame ? RemoteControlState.fullFrameKey : event.key;
    final sequence = event.tile.sequence;

    final newest = _newestSequence[key];
    if ((newest != null && newest > sequence) || (!isFullFrame && sequence < _fullFrameSequence)) {
      event.tile.image.dispose();
      return;
    }
    _newestSequence[key] = sequence;

    final tiles = Map<int, RemoteControlTileImage>.of(state.tiles);

    if (isFullFrame) {
      _fullFrameSequence = sequence;
      for (final stale in tiles.values) {
        stale.image.dispose();
      }
      tiles.clear();
    } else {
      tiles.remove(key)?.image.dispose();
    }

    tiles[key] = event.tile;
    emit(state.copyWith(tiles: tiles));
  }

  Future<void> _onStreamClosed(RemoteControlStreamClosed event, Emitter<RemoteControlState> emit) async {
    await _detachStream();
    // Not treated as the end of the session here: the socket closing says nothing about why, and
    // the poll is about to read the reason off the session row.
    add(const RemoteControlSessionPolled());
  }

  void _onInputSent(RemoteControlInputSent event, Emitter<RemoteControlState> emit) {
    _stream?.send(event.input);
  }

  void _attachStream(String sessionId) {
    if (_stream != null) return;

    final stream = _openStream(sessionId);
    _stream = stream;

    _subscription = stream.updates.listen(
      (update) async {
        if (isClosed) return;

        switch (update) {
          case RemoteDisplayGeometry():
            add(RemoteControlGeometryChanged(update));

          case RemoteShellInfo():
            add(RemoteControlShellStarted(update));

          case RemoteShellOutput():
            // Straight onto the stream rather than through an event: these are bytes to be written
            // into a terminal, not state to be diffed, and a `yes` loop would otherwise put a
            // thousand events a second through the bloc for a picture nothing compares.
            if (!_shellOutput.isClosed) _shellOutput.add(update.bytes);

          case RemoteScreenTile():
            // Decoded here rather than in the painter: `instantiateImageCodec` is asynchronous, and
            // a painter cannot await. On the web this is the browser's own JPEG decoder.
            try {
              final codec = await ui.instantiateImageCodec(update.jpeg);
              final frame = await codec.getNextFrame();
              if (isClosed) {
                frame.image.dispose();
                return;
              }

              add(RemoteControlTileDecoded(
                _tileKey(update.x, update.y),
                RemoteControlTileImage(
                  image: frame.image,
                  x: update.x,
                  y: update.y,
                  width: update.width,
                  height: update.height,
                  sequence: update.sequence,
                ),
              ));
            } on Exception {
              // One tile that would not decode is one stale rectangle until the host changes it
              // again — not a reason to end a session somebody is working in.
            }
        }
      },
      onDone: () {
        if (!isClosed) add(const RemoteControlStreamClosed());
      },
    );
  }

  Future<void> _detachStream() async {
    await _subscription?.cancel();
    _subscription = null;
    await _stream?.close();
    _stream = null;
  }

  /// Tile positions are packed into one int so the map has a cheap key. The image is capped at
  /// 65535 in each direction by the protocol's `u16` fields, so 16 bits each is exact.
  static int _tileKey(int x, int y) => (x << 16) | y;

  @override
  Future<void> close() async {
    await _detachStream();
    await _shellOutput.close();

    // ui.Image holds a native texture that the garbage collector does not account for; a session
    // closed without this leaks the whole last screen.
    for (final tile in state.tiles.values) {
      tile.image.dispose();
    }

    return super.close();
  }
}
