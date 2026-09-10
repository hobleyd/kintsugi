/// A whole-page navigation, as opposed to a request.
///
/// Sign-in and sign-out both need one: the response is a redirect to the identity provider's own
/// origin, which a `fetch` cannot usefully follow. Behind an interface so the BLoCs that trigger
/// them stay testable off the web target — the browser implementation is the only file in this
/// app that imports `package:web`.
abstract interface class PageNavigator {
  /// Navigates the current page to [url].
  void go(String url);

  /// Navigates by submitting a POST to [url].
  ///
  /// Sign-out is a POST — as it was when it was a Razor form — because a GET that ends a session
  /// can be triggered by any page that can get the browser to load a URL.
  void post(String url);

  /// Opens [url] in a new tab, leaving this one where it is.
  ///
  /// Distinct from [go], which replaces the current page. Used for links out to somebody else's
  /// site — NVD's page for a CVE, say — where navigating away would lose whatever the
  /// administrator was in the middle of reading.
  void openInNewTab(String url);
}
