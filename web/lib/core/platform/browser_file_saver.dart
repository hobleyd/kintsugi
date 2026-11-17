import 'dart:js_interop';
import 'dart:typed_data';

import 'package:web/web.dart' as web;

import 'file_saver.dart';

/// The real [FileSaver]: a Blob behind a temporary link that is clicked once and thrown away. One of
/// the few files in this app that touch the DOM. Nothing leaves the browser: the bytes are made on
/// this machine and saved to it.
class BrowserFileSaver implements FileSaver {
  const BrowserFileSaver();

  @override
  Future<void> save(Uint8List bytes, String fileName, {String mimeType = 'application/octet-stream'}) async {
    final blob = web.Blob([bytes.toJS].toJS, web.BlobPropertyBag(type: mimeType));
    final url = web.URL.createObjectURL(blob);
    final link = web.HTMLAnchorElement()
      ..href = url
      ..download = fileName
      ..style.display = 'none';
    web.document.body!.append(link);
    link.click();
    link.remove();
    web.URL.revokeObjectURL(url);
  }
}
