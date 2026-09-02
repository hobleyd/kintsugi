import '../../core/network/api_client.dart';
import '../../core/platform/page_navigator.dart';
import '../../domain/entities/session.dart';
import '../../domain/repositories/repositories.dart';
import '../models/session_mapper.dart';

class SessionRepositoryImpl implements SessionRepository {
  const SessionRepositoryImpl(this._api, this._navigator);

  final ApiClient _api;
  final PageNavigator _navigator;

  @override
  Future<Session> read() async =>
      sessionFromJson(await _api.getJson('/api/session') as Map<String, dynamic>);

  @override
  void signIn({String? returnPath}) {
    final query = returnPath == null || returnPath.isEmpty
        ? ''
        : '?returnUrl=${Uri.encodeQueryComponent(returnPath)}';
    _navigator.go('/api/auth/challenge$query');
  }

  @override
  void signOut() => _navigator.post('/api/auth/logout');
}
