/// The browser's own full-screen mode, behind an interface.
///
/// Exists for exactly one screen — the remote-control viewer, where the host's desktop is the whole
/// point of the page and a sidebar around it is wasted width. It is an interface for the same reason
/// [PageNavigator] is: `package:web` is unavailable under `flutter test`, where `kIsWeb` is false, so
/// a widget that reached for the DOM directly could not be pumped at all.
///
/// **Two things about the browser's rules shape this, and neither is negotiable.**
/// `requestFullscreen` needs *transient user activation* — roughly five seconds after a click in
/// Chrome — so it can only be asked for on the same turn as the gesture that led here, never after
/// an await on something a person has to answer. And it can be left by pressing Escape without the
/// page being told first, which is why [isFullScreen] is asked rather than remembered and
/// [onChanged] exists at all.
abstract interface class FullScreenController {
  /// Whether the document is currently full-screen.
  bool get isFullScreen;

  /// Asks the browser for full screen. Returns whether it agreed.
  ///
  /// Refusal is ordinary rather than exceptional: a request made outside the activation window is
  /// declined, and the caller's job is to offer a button instead of reporting an error.
  Future<bool> enter();

  Future<void> exit();

  /// Fires whenever the browser enters or leaves full screen, including by Escape or F11.
  Stream<bool> get onChanged;
}
