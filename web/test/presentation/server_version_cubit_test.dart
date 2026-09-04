import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/server_info_usecases.dart';
import 'package:kintsugi_web/presentation/shell/server_version_cubit.dart';

class _FakeServerInfoRepository implements ServerInfoRepository {
  _FakeServerInfoRepository(this._answer);

  final Future<String> Function() _answer;

  @override
  Future<String> version() => _answer();
}

void main() {
  test('starts empty and emits the version once the server answers', () async {
    final cubit = ServerVersionCubit(GetServerVersion(_FakeServerInfoRepository(() async => '1.0.0')));

    expect(cubit.state, isNull);
    await expectLater(cubit.stream, emits('1.0.0'));
  });

  test('stays empty rather than surfacing an error when the route fails', () async {
    // The version is a label under a logo; the failures this route has are the ones every route
    // has, and each of those is already reported somewhere better.
    final cubit = ServerVersionCubit(
      GetServerVersion(_FakeServerInfoRepository(() async => throw const ApiException('down'))),
    );

    await Future<void>.delayed(Duration.zero);

    expect(cubit.state, isNull);
    expect(cubit.isClosed, isFalse);
  });
}
