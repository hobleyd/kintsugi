import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'core/di/injection.dart';
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
  )..add(const SessionRequested());

  // Built once, from the session bloc, because the router's redirect is the session gate and
  // rebuilding it would drop the browser's history.
  late final _router = createRouter(_sessionBloc);

  @override
  void dispose() {
    _sessionBloc.close();
    _router.dispose();
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
          ),
        ),
      );
}
