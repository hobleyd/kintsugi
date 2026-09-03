import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:web_socket_channel/web_socket_channel.dart';

import '../../core/network/api_client.dart';
import '../../domain/entities/remote_control_session.dart';
import '../../domain/repositories/repositories.dart';
import '../models/remote_control_mapper.dart';

class RemoteControlRepositoryImpl implements RemoteControlRepository {
  const RemoteControlRepositoryImpl(this._api);

  /// Everything under `/api/admin/`, and the prefix is load-bearing: the agent's own socket lives at
  /// `/api/remote-control`, which is *inside* nginx's client-certificate regex, so a browser-driven
  /// route on that path would demand a fleet certificate this browser has not got.
  static const _base = '/api/admin/remote-control/sessions';

  final ApiClient _api;

  @override
  Future<RemoteControlSession> request(String hostId) async => remoteControlSessionFromJson(
        (await _api.postJson(_base, body: {'hostId': hostId})) as Map<String, dynamic>,
      );

  @override
  Future<RemoteControlSession?> session(String id) async {
    final json = await _api.getJson('$_base/${Uri.encodeComponent(id)}');
    return json is Map<String, dynamic> ? remoteControlSessionFromJson(json) : null;
  }

  @override
  Future<void> end(String id) => _api.delete('$_base/${Uri.encodeComponent(id)}');

  @override
  RemoteControlStream openStream(String sessionId) => _WebSocketRemoteControlStream(sessionId);
}

/// The media channel, over a same-origin WebSocket.
///
/// Same-origin is what makes this work at all without any credential handling: the session cookie
/// `[RequireAdminSession]` reads rides along on the upgrade request exactly as it does on every
/// other call this app makes. A socket to another origin would need a token in the URL, which is a
/// credential in something that gets logged.
class _WebSocketRemoteControlStream implements RemoteControlStream {
  _WebSocketRemoteControlStream(String sessionId) {
    _channel = WebSocketChannel.connect(_streamUri(sessionId));

    _subscription = _channel.stream.listen(
      _onMessage,
      // Both are the same thing to a viewer — the session is over — and the screen learns why from
      // the session row it is polling rather than from a socket error string.
      onError: (Object _) => _updates.close(),
      onDone: _updates.close,
      cancelOnError: true,
    );
  }

  late final WebSocketChannel _channel;
  late final StreamSubscription<dynamic> _subscription;
  final StreamController<RemoteScreenUpdate> _updates = StreamController<RemoteScreenUpdate>();

  bool _closed = false;

  @override
  Stream<RemoteScreenUpdate> get updates => _updates.stream;

  @override
  void send(RemoteInput input) {
    if (_closed) return;
    _channel.sink.add(jsonEncode(remoteInputToJson(input)));
  }

  @override
  Future<void> close() async {
    if (_closed) return;
    _closed = true;

    await _subscription.cancel();
    await _channel.sink.close();
    if (!_updates.isClosed) await _updates.close();
  }

  void _onMessage(dynamic message) {
    if (_updates.isClosed) return;

    // Text is control (the display geometry); binary is a screen tile. WebSocket distinguishes the
    // two natively, which is why the media protocol does not need a discriminator byte of its own.
    if (message is String) {
      final update = remoteTextUpdateFromJson(message);
      if (update != null) _updates.add(update);
      return;
    }

    if (message is List<int>) {
      final tile = remoteTileFromBytes(
        message is Uint8List ? message : Uint8List.fromList(message),
      );
      if (tile != null) _updates.add(tile);
    }
  }

  /// Derived from the page's own address rather than configured, for the same reason every other
  /// call in this app is a relative path: nginx serves this bundle and proxies the API behind it.
  static Uri _streamUri(String sessionId) {
    final page = Uri.base;

    return Uri(
      scheme: page.scheme == 'https' ? 'wss' : 'ws',
      host: page.host,
      port: page.hasPort ? page.port : null,
      path: '/api/admin/remote-control/sessions/${Uri.encodeComponent(sessionId)}/stream',
    );
  }
}
