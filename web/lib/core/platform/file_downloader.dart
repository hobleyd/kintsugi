/// Hands a file to the browser's download, as opposed to showing it.
///
/// Behind an interface for the reason `PageNavigator` is: the screens that export stay testable off
/// the web target, and the browser implementation is the only file that builds a `Blob`.
abstract interface class FileDownloader {
  /// Saves [content] as [filename]. [mimeType] is what the browser is told it is.
  void save(String filename, String mimeType, String content);
}
