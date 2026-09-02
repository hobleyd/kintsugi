import 'package:web/web.dart' as web;

import 'page_navigator.dart';

/// The real [PageNavigator]. The only place this app touches the DOM directly.
class BrowserPageNavigator implements PageNavigator {
  const BrowserPageNavigator();

  @override
  void go(String url) => web.window.location.assign(url);

  @override
  void post(String url) {
    // A form rather than a request, for the same reason [go] exists: what comes back is a redirect
    // chain through the identity provider's end-session endpoint and then back to this origin, and
    // the browser has to be the thing following it.
    final form = web.document.createElement('form') as web.HTMLFormElement
      ..method = 'post'
      ..action = url;
    web.document.body!.append(form);
    form.submit();
  }
}
