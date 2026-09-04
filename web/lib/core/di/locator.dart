import 'package:get_it/get_it.dart';

/// The service locator every screen resolves its use cases from.
///
/// In its own file, apart from `injection.dart`, because of what that file imports:
/// `BrowserPageNavigator`, and through it `package:web`, which does not compile for the VM that
/// `flutter test` runs on. A screen that imported the composition root to reach `locator` dragged
/// that in, and with it every widget in `presentation/` became impossible to pump in a test — the
/// resizer bug in `instructions_panel.dart` shipped for exactly that reason. Screens import this
/// file; only `main.dart` imports `injection.dart`. A test registers fakes here and pumps the
/// real widget.
final locator = GetIt.instance;
