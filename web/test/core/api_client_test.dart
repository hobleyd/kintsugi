import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kintsugi_web/core/network/api_client.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/core/network/unauthorized_notifier.dart';

ApiClient clientReturning(
  Object? body, {
  int status = 200,
  String contentType = 'application/json',
  void Function(http.Request request)? onRequest,
}) =>
    ApiClient(
      httpClient: MockClient((request) async {
        onRequest?.call(request);
        return http.Response(
          body is String ? body : jsonEncode(body),
          status,
          headers: {'content-type': contentType},
        );
      }),
    );

void main() {
  test('decodes a JSON body', () async {
    final api = clientReturning({'hostname': 'alpha'});
    expect(await api.getJson('/api/hosts'), {'hostname': 'alpha'});
  });

  test('drops null query parameters instead of sending them empty', () async {
    Uri? seen;
    final api = clientReturning(
      const <String, Object>{},
      onRequest: (request) => seen = request.url,
    );

    await api.getJson('/api/upgrade-paths/prompt', query: {'applicationName': 'Firefox', 'platform': null});

    // Sending platform= empty is not the same request as omitting it: the query handler treats a
    // null platform as "whichever one research would use" and an empty string as a platform named
    // "".
    expect(seen!.queryParameters, {'applicationName': 'Firefox'});
  });

  test('raises UnauthorizedApiException on a 401 so the app can re-read the session', () async {
    final api = clientReturning(
      {'title': 'Not signed in.', 'detail': 'This action requires a signed-in administrator.'},
      status: 401,
      contentType: 'application/problem+json',
    );

    // Distinct from a general failure because it is not an error to report: it means the session
    // has lapsed, and the right response is to route to the sign-in screen.
    await expectLater(
      api.getJson('/api/hosts'),
      throwsA(isA<UnauthorizedApiException>()),
    );
  });

  test("prefers problem+json's detail over the bare status", () async {
    final api = clientReturning(
      {'title': 'Request could not be processed.', 'detail': 'A tenant ID is required for Microsoft Entra.'},
      status: 400,
      contentType: 'application/problem+json',
    );

    await expectLater(
      api.putJson('/api/admin/settings/authentication', body: const {}),
      throwsA(
        isA<ApiException>().having(
          (e) => e.message,
          'message',
          'A tenant ID is required for Microsoft Entra.',
        ),
      ),
    );
  });

  test('prefers per-field validation messages over the generic title above them', () async {
    final api = clientReturning(
      {
        'title': 'One or more validation errors occurred.',
        'errors': {
          'ClientId': ['A client ID is required.'],
        },
      },
      status: 400,
      contentType: 'application/problem+json',
    );

    await expectLater(
      api.putJson('/api/admin/settings/authentication', body: const {}),
      throwsA(
        isA<ApiException>()
            .having((e) => e.message, 'message', 'A client ID is required.')
            .having((e) => e.validationErrors['ClientId'], 'field errors', ['A client ID is required.']),
      ),
    );
  });

  test('reports a non-JSON 2xx as something answering instead of the API', () async {
    // Almost always an nginx or proxy page. Letting a jsonDecode failure bubble up would present a
    // parser error where the real answer is "something in front of the API answered this".
    final api = clientReturning('<html>502 Bad Gateway</html>', contentType: 'text/html');

    await expectLater(
      api.getJson('/api/hosts'),
      throwsA(isA<ApiException>().having((e) => e.message, 'message', contains('rather than JSON'))),
    );
  });

  test('reports a transport failure without leaking the URL-shaped default message', () async {
    final api = ApiClient(
      httpClient: MockClient((_) async => throw http.ClientException('Failed host lookup')),
    );

    await expectLater(
      api.getJson('/api/hosts'),
      throwsA(isA<ApiException>().having(
        (e) => e.message,
        'message',
        'Could not reach the server (GET /api/hosts).',
      )),
    );
  });

  test('returns null for an empty body, which DELETE answers with', () async {
    final api = ApiClient(httpClient: MockClient((_) async => http.Response('', 204)));
    await expectLater(api.delete('/api/hosts/abc'), completes);
  });

  group('the 401 announcement', () {
    test('fires for an ordinary route, so the app can get back to a sign-in screen', () async {
      final notifier = UnauthorizedNotifier();
      var announced = 0;
      notifier.stream.listen((_) => announced++);

      final api = ApiClient(
        httpClient: MockClient((_) async => http.Response('', 401)),
        unauthorizedNotifier: notifier,
      );

      await expectLater(api.getJson('/api/hosts'), throwsA(isA<UnauthorizedApiException>()));
      await Future<void>.delayed(Duration.zero);

      expect(announced, 1);
      await notifier.dispose();
    });

    test('does not fire for /api/session itself', () async {
      // Announcing one would make the session bloc re-read this same route, which would 401
      // again, which would announce again.
      final notifier = UnauthorizedNotifier();
      var announced = 0;
      notifier.stream.listen((_) => announced++);

      final api = ApiClient(
        httpClient: MockClient((_) async => http.Response('', 401)),
        unauthorizedNotifier: notifier,
      );

      await expectLater(api.getJson('/api/session'), throwsA(isA<UnauthorizedApiException>()));
      await Future<void>.delayed(Duration.zero);

      expect(announced, 0);
      await notifier.dispose();
    });
  });
}
