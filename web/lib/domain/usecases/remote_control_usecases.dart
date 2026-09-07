import '../entities/enums.dart';
import '../entities/remote_control_session.dart';
import '../repositories/repositories.dart';

/// Opens a session of the given kind on a host.
///
/// Returns immediately with a session whose consent is still pending — or, if no agent was
/// connected, one already answered as unreachable.
///
/// For a **screen** session, nothing is captured and nothing is shown to the requesting
/// administrator until the person at the keyboard says yes. For a **shell** session nobody is
/// asked at all, which is the deliberate difference between the two: it is the SSH-equivalent
/// access an administrator of this fleet already has, and the audit row is what stands in for the
/// dialog. See `RemoteControlSessionKind` on the server.
class RequestRemoteControlSession {
  const RequestRemoteControlSession(this._repository);

  final RemoteControlRepository _repository;

  Future<RemoteControlSession> call(String hostId, RemoteControlSessionKind kind) =>
      _repository.request(hostId, kind);
}

class GetRemoteControlSession {
  const GetRemoteControlSession(this._repository);

  final RemoteControlRepository _repository;

  Future<RemoteControlSession?> call(String id) => _repository.session(id);
}

class EndRemoteControlSession {
  const EndRemoteControlSession(this._repository);

  final RemoteControlRepository _repository;

  Future<void> call(String id) => _repository.end(id);
}

/// Opens the screen-and-input channel for a session that has been granted.
class OpenRemoteControlStream {
  const OpenRemoteControlStream(this._repository);

  final RemoteControlRepository _repository;

  RemoteControlStream call(String sessionId) => _repository.openStream(sessionId);
}
