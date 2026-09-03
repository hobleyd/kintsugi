import '../entities/remote_control_session.dart';
import '../repositories/repositories.dart';

/// Asks a host's agent to put the consent dialog in front of whoever is sitting at it.
///
/// Returns immediately with a session whose consent is still pending — or, if no agent was
/// connected, one already answered as unreachable. Nothing is captured and nothing is shown to the
/// requesting administrator until the person at the keyboard says yes.
class RequestRemoteControlSession {
  const RequestRemoteControlSession(this._repository);

  final RemoteControlRepository _repository;

  Future<RemoteControlSession> call(String hostId) => _repository.request(hostId);
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
