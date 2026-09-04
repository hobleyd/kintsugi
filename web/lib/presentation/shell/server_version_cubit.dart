import 'package:flutter_bloc/flutter_bloc.dart';

import '../../domain/usecases/server_info_usecases.dart';

/// The server build's version, read once for the sidebar's brand block.
///
/// Null until it arrives and null if it never does. Failure is deliberately silent: the version is
/// a label under a logo, and the only failures this route has are the ones every other route has
/// too — the server is down (the cannot-reach screen says so) or the session has expired (a 401
/// here reaches `SessionBloc` through `UnauthorizedNotifier` like any other). A second copy of
/// either message beside the logo would add nothing.
class ServerVersionCubit extends Cubit<String?> {
  ServerVersionCubit(GetServerVersion getServerVersion) : super(null) {
    _load(getServerVersion);
  }

  Future<void> _load(GetServerVersion getServerVersion) async {
    try {
      final version = await getServerVersion();
      if (!isClosed) {
        emit(version);
      }
    } on Exception {
      // See the class comment.
    }
  }
}
