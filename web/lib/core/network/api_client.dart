import 'dart:convert';

import 'package:http/http.dart' as http;

import 'api_exception.dart';

/// The one place this app talks to the API.
///
/// Same-origin by design: nginx serves this bundle and proxies `/api` to the ASP.NET Core app
/// behind it, so every path here is relative and the session cookie rides along without any CORS
/// or credential configuration. That is also why sign-in works at all — the cookie
/// `[RequireAdminSession]` reads is set on this origin by `/signin-oidc`.
///
/// [http.Client] rather than a browser client named outright, so this file stays compilable off
/// the web target and the unit tests can hand it a fake.
class ApiClient {
  ApiClient({http.Client? httpClient}) : _http = httpClient ?? http.Client();

  final http.Client _http;

  Future<Object?> getJson(String path, {Map<String, String?>? query}) =>
      _send('GET', path, query: query);

  Future<Object?> postJson(String path, {Object? body}) => _send('POST', path, body: body);

  Future<Object?> putJson(String path, {Object? body}) => _send('PUT', path, body: body);

  Future<void> delete(String path) => _send('DELETE', path);

  Future<Object?> _send(String method, String path, {Object? body, Map<String, String?>? query}) async {
    final uri = Uri.parse(path).replace(
      queryParameters: query == null
          ? null
          : {
              for (final entry in query.entries)
                if (entry.value != null) entry.key: entry.value!,
            },
    );

    final request = http.Request(method, uri)
      ..headers['Accept'] = 'application/json';
    if (body != null) {
      request.headers['Content-Type'] = 'application/json';
      request.body = jsonEncode(body);
    }

    final http.Response response;
    try {
      response = await http.Response.fromStream(await _http.send(request));
    } catch (error) {
      // A dropped connection, a DNS failure, or the browser refusing the request outright. There
      // is no status and no body to read, so say what happened rather than surfacing a
      // ClientException's own wording, which names the URL and nothing useful about the cause.
      throw ApiException('Could not reach the server ($method $path).');
    }

    if (response.statusCode == 401) {
      throw UnauthorizedApiException(_problemMessage(response) ?? 'Not signed in.');
    }

    if (response.statusCode < 200 || response.statusCode >= 300) {
      throw ApiException(
        _problemMessage(response) ?? 'HTTP ${response.statusCode} ${response.reasonPhrase ?? ''}'.trim(),
        statusCode: response.statusCode,
        validationErrors: _validationErrors(response),
      );
    }

    if (response.body.isEmpty) return null;

    // A non-JSON body on a 2xx is almost always an nginx or proxy page rather than anything the
    // API returned — reported as such, because letting a jsonDecode failure bubble up presents a
    // parser error where the real answer is "something in front of the API answered this".
    if (!_isJson(response)) {
      throw ApiException(
        'The server answered $method $path with ${response.headers['content-type'] ?? 'no content type'} '
        'rather than JSON. Something in front of the API is most likely answering instead of it.',
        statusCode: response.statusCode,
      );
    }

    return jsonDecode(utf8.decode(response.bodyBytes));
  }

  static bool _isJson(http.Response response) =>
      (response.headers['content-type'] ?? '').contains('json');

  /// The human-readable half of an `application/problem+json` body, if there is one.
  static String? _problemMessage(http.Response response) {
    if (!_isJson(response)) return null;
    try {
      final decoded = jsonDecode(utf8.decode(response.bodyBytes));
      if (decoded is! Map<String, dynamic>) return null;

      // Prefer the per-field messages when there are any: "A tenant ID is required for Microsoft
      // Entra" is the answer, and the generic "One or more validation errors occurred." title
      // above it is not.
      final errors = _errorsMap(decoded);
      if (errors.isNotEmpty) {
        return errors.values.expand((messages) => messages).join(' ');
      }

      final detail = decoded['detail'];
      if (detail is String && detail.isNotEmpty) return detail;
      final title = decoded['title'];
      if (title is String && title.isNotEmpty) return title;
      return null;
    } catch (_) {
      return null;
    }
  }

  static Map<String, List<String>> _validationErrors(http.Response response) {
    if (!_isJson(response)) return const {};
    try {
      final decoded = jsonDecode(utf8.decode(response.bodyBytes));
      return decoded is Map<String, dynamic> ? _errorsMap(decoded) : const {};
    } catch (_) {
      return const {};
    }
  }

  static Map<String, List<String>> _errorsMap(Map<String, dynamic> problem) {
    final errors = problem['errors'];
    if (errors is! Map<String, dynamic>) return const {};
    return {
      for (final entry in errors.entries)
        entry.key: entry.value is List
            ? (entry.value as List).map((e) => e.toString()).toList()
            : [entry.value.toString()],
    };
  }
}
