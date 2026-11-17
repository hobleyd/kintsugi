import 'dart:typed_data';

/// Hands a file to the browser as a download, behind an interface.
///
/// An interface for the same reason [PageNavigator] and [FullScreenController] are: `package:web` is
/// unavailable under `flutter test`, so a screen that reached for the DOM directly could not be
/// pumped, and the PDF a screen builds could not be checked without a browser.
abstract interface class FileSaver {
  /// Offers [bytes] to the person as a file called [fileName].
  Future<void> save(Uint8List bytes, String fileName, {String mimeType});
}
