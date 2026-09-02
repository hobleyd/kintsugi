# Kintsugi admin UI

The browser UI for Kintsugi, as a Flutter web application. It is served as static files by the
nginx container — `nginx/Dockerfile` compiles this directory and bakes the bundle into that image —
and talks to the ASP.NET Core API on the same origin. See the "The admin UI" section of the
repository's `CLAUDE.md` for the reasoning behind the parts that are not obvious from the code.

## Working on it

```bash
flutter analyze
flutter test
flutter build web --release          # what nginx/Dockerfile runs

# Against a running backend. `docker compose up -d --build` first: this serves the UI itself but
# proxies nothing, so every API call needs the real server behind it.
flutter run -d chrome
```

`flutter run` serves on its own port, which means API calls are cross-origin and the session cookie
will not ride along — sign-in will not work in that mode. Point it at a server with authentication
disabled, or use `docker compose up -d --build` to exercise the real thing.

## Layout

```
lib/
  core/          transport, theme, router, polling, DI. injection.dart is the only file
                 that names a concrete implementation.
  domain/        entities, repository interfaces, use cases. No JSON, no HTTP.
  data/          mappers from the API's JSON, and the repository implementations.
  presentation/  one directory per screen: its BLoC(s), its screen, its widgets.
test/            unit tests, mirroring lib/'s layout.
```

The dependency arrow points inwards: `presentation` depends on `domain`, `data` implements
`domain`, and nothing in `domain` knows how anything is transported.

## Two things that will bite

**Enums cross the wire as names or as ordinals, depending on the type.** Which is which, and why it
must not be unified, is in `lib/core/network/json_reader.dart`. Declaration order in
`lib/domain/entities/enums.dart` is load-bearing.

**The theme key is duplicated in `web/index.html` by hand.** `shared_preferences` namespaces its
keys under `flutter.`, so the inline script that paints the background before this app boots looks
for `flutter.kintsugi-theme`. Renaming it in `ThemeCubit` means renaming it there too.
