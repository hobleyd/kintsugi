import '../entities/session.dart';
import '../repositories/repositories.dart';

/// Reads the bootstrap state the app routes on.
class ReadSession {
  const ReadSession(this._repository);

  final SessionRepository _repository;

  Future<Session> call() => _repository.read();
}

/// Starts the sign-in round trip.
class SignIn {
  const SignIn(this._repository);

  final SessionRepository _repository;

  /// [returnPath] is where the browser lands once the provider has sent it back. The server
  /// rejects anything that is not a local path, so a hostile value cannot turn this into an open
  /// redirect.
  void call({String? returnPath}) => _repository.signIn(returnPath: returnPath);
}

class SignOut {
  const SignOut(this._repository);

  final SessionRepository _repository;

  void call() => _repository.signOut();
}
