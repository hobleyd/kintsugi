/// A request that did not come back with what was asked for.
///
/// [message] is meant to be shown to an operator as-is. The API answers failures with
/// `application/problem+json` (see `ExceptionHandlingMiddleware`), so where there is a `detail` or
/// `title` that is what this carries — "A tenant ID is required for Microsoft Entra" rather than
/// "HTTP 400".
class ApiException implements Exception {
  const ApiException(this.message, {this.statusCode, this.validationErrors = const {}});

  final String message;
  final int? statusCode;

  /// Field-level failures, keyed by property name, from a `ValidationProblemDetails` response.
  ///
  /// This is what `ValidationBehaviour`'s FluentValidation failures arrive as, and it is why the
  /// settings screens can put an error under the field that caused it — the Razor forms could only
  /// ever render one flat list, because `ModelState.AddModelError(string.Empty, ...)` threw the
  /// property name away.
  final Map<String, List<String>> validationErrors;

  @override
  String toString() => message;
}

/// The caller is not signed in.
///
/// Distinct from a general failure because it is not an error to report to the operator — it means
/// the session has lapsed, and the right response is to re-read `GET /api/session` and let the app
/// route to the sign-in screen. Raised for a 401 from any route carrying `[RequireAdminSession]`.
class UnauthorizedApiException extends ApiException {
  const UnauthorizedApiException(super.message) : super(statusCode: 401);
}
