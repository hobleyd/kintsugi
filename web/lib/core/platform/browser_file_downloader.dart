import 'dart:js_interop';

import 'package:web/web.dart' as web;

import 'file_downloader.dart';

/// The real [FileDownloader]: a `Blob`, an object URL and a click on a link, which is the way a
/// page without a server round-trip starts a download.
class BrowserFileDownloader implements FileDownloader {
  const BrowserFileDownloader();

  @override
  void save(String filename, String mimeType, String content) {
    final blob = web.Blob([content.toJS].toJS, web.BlobPropertyBag(type: mimeType));
    final url = web.URL.createObjectURL(blob);
    final link = web.document.createElement('a') as web.HTMLAnchorElement
      ..href = url
      ..download = filename;
    web.document.body!.append(link);
    link.click();
    link.remove();
    web.URL.revokeObjectURL(url);
  }
}
