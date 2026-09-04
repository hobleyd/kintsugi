import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'core/di/injection.dart';
import 'core/di/locator.dart';
import 'core/network/unauthorized_notifier.dart';
import 'core/router/app_router.dart';
import 'core/theme/app_theme.dart';
import 'core/theme/theme_cubit.dart';
import 'domain/usecases/session_usecases.dart';
import 'presentation/session/session_bloc.dart';

Future<void> main() async {
  await configureDependencies();
  runApp(const KintsugiApp());
}

/// The Kintsugi administration UI.
///
/// Served as static files by the nginx container and talking to the ASP.NET Core API on the same
/// origin. Two things sit above every screen: the theme, and the session — the latter because what
/// to render at all depends on whether sign-in is configured, required and done, and that answer
/// arrives from `GET /api/session` rather than from a redirect this bundle can never receive.
class KintsugiApp extends StatefulWidget {
  const KintsugiApp({super.key});

  @override
  State<KintsugiApp> createState() => _KintsugiAppState();
}

class _KintsugiAppState extends State<KintsugiApp> {
  late final SessionBloc _sessionBloc = SessionBloc(
    readSession: locator<ReadSession>(),
    signIn: locator<SignIn>(),
    signOut: locator<SignOut>(),
    unauthorizedNotifier: locator<UnauthorizedNotifier>(),
  )..add(const SessionRequested());

  // Built once, from the session bloc, because the router's redirect is the session gate and
  // rebuilding it would drop the browser's history.
  late final _router = createRouter(_sessionBloc);

  // The app-wide SelectionArea's focus node, supplied rather than left to default for the sake of
  // `skipTraversal`. Two reasons, and the second is the one that bites: Tab should move between a
  // form's fields rather than stopping on a selection region that spans the whole page, and — on
  // web only — SelectableRegion wraps its child in a Stack holding an HtmlElementView for the
  // browser's own right-click menu. The browser hands Flutter a view-focus change before that
  // Stack's first layout, and the traversal sort reads every node's `rect`, which asserts
  // `hasSize` on a render object that has none yet. Skipping traversal keeps the node out of that
  // sort. Deleting this argument brings back a first-frame exception that no test here can see:
  // `kIsWeb` is false under `flutter test`, so the VM never builds that Stack at all.
  final _selectionFocusNode = FocusNode(skipTraversal: true, debugLabel: 'selection');

  @override
  void dispose() {
    _sessionBloc.close();
    _router.dispose();
    _selectionFocusNode.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => MultiBlocProvider(
        providers: [
          BlocProvider.value(value: _sessionBloc),
          BlocProvider(create: (_) => ThemeCubit(locator<SharedPreferences>())),
        ],
        child: BlocBuilder<ThemeCubit, ThemeMode>(
          builder: (context, themeMode) => MaterialApp.router(
            title: 'Kintsugi',
            debugShowCheckedModeBanner: false,
            theme: AppTheme.light(),
            darkTheme: AppTheme.dark(),
            themeMode: themeMode,
            routerConfig: _router,
            // Every screen's text is selectable and copyable, which on this client is a decision
            // rather than a default: Flutter web paints its text into a canvas, so there is no
            // DOM for the browser's own selection to act on and nothing is selectable unless a
            // SelectionArea says so. It goes in `builder` rather than around AppShell because
            // that is the one place inside MaterialApp's Theme and Localizations (the selection
            // toolbar needs both) and *above* the Navigator, so it covers the routes the shell
            // does not — sign-in, the cannot-reach-Kintsugi screen — and both dialogs, which are
            // routes pushed on that same Navigator. See script_dialog.dart, whose script is plain
            // Text for this reason.
            //
            // Overlay.wrap is load-bearing rather than decoration: SelectableRegion asserts an
            // Overlay ancestor (it floats its toolbar and magnifier in one), and `builder` runs
            // *above* the Navigator that would otherwise provide it — so a bare SelectionArea
            // here throws "No Overlay widget found" on the first frame. In debug only, since it
            // is an assert, which is exactly why the release bundle compiling proves nothing;
            // test/presentation/text_selection_test.dart pumps this same arrangement.
            builder: (context, child) => Overlay.wrap(
              child: SelectionArea(focusNode: _selectionFocusNode, child: child!),
            ),
          ),
        ),
      );
}
