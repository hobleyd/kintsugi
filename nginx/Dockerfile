# The nginx image, with the admin UI baked in.
#
# nginx now serves two things: the Flutter web bundle for the browser, and the reverse proxy in
# front of the API. That is why this image is built rather than pulled — `docker compose up -d
# --build` has to keep being the one documented way to run the system, and a bundle built on
# somebody's laptop and mounted in would make that instruction a lie on a clean checkout.

# ---------------------------------------------------------------------------------------------
# Stage 1: compile the Flutter bundle.
# ---------------------------------------------------------------------------------------------
#
# Pinned to linux/amd64, and it has to be: Flutter publishes exactly one Linux SDK archive and it
# is x64 — there is no flutter_linux_arm64_*.tar.xz to fetch (checked; it 404s). On an x86 build
# host this is native. On an Apple Silicon machine it runs under emulation and the compile takes
# minutes rather than seconds, which is worth knowing before assuming the build has hung. The
# runtime stage below is deliberately *not* pinned, so the image that actually runs is native to
# whatever built it; only files cross between them.
#
# Through a build arg rather than written inline, for two reasons: buildx warns on a constant
# --platform (FromPlatformFlagConstDisallowed) because a hardcoded one is usually a mistake, and
# this way the pin can be overridden — `--build-arg WEBBUILD_PLATFORM=linux/arm64` — the day
# Flutter ships an arm64 Linux SDK, without editing this file.
ARG WEBBUILD_PLATFORM=linux/amd64
FROM --platform=${WEBBUILD_PLATFORM} debian:12-slim AS webbuild

# Pinned, and pinned deliberately: the SDK is a compiler, so "whatever stable is today" would make
# this image's contents depend on the day it was built. Bumping it is a deliberate act — check
# web/pubspec.yaml's `environment: sdk:` constraint still holds when you do.
ARG FLUTTER_VERSION=3.47.0

# Fetched from Google's own release bucket rather than a community Flutter image. A third-party
# base image would put an unaudited party in the path of the bytes the browser executes, for no
# saving beyond a shorter Dockerfile.
ARG FLUTTER_SDK_URL=https://storage.googleapis.com/flutter_infra_release/releases/stable/linux/flutter_linux_${FLUTTER_VERSION}-stable.tar.xz

# curl and ca-certificates to fetch the SDK; git because the Flutter tool reads its own revision
# from the checkout and refuses to run without it; xz-utils to unpack the archive.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl git xz-utils \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL "${FLUTTER_SDK_URL}" -o /tmp/flutter.tar.xz \
    && tar -xJf /tmp/flutter.tar.xz -C /opt \
    && rm /tmp/flutter.tar.xz

ENV PATH="/opt/flutter/bin:${PATH}"

# The SDK ships owned by a user that does not exist here, and the tool writes into its own
# directory (the .version_check stamp, the Dart snapshot cache). Marking the checkout safe is what
# stops git refusing to report the revision the tool needs.
RUN git config --global --add safe.directory /opt/flutter \
    && flutter config --no-analytics \
    && flutter precache --web

WORKDIR /src

# Dependencies first, so an edit to lib/ does not re-resolve the whole pub cache. pubspec.lock is
# copied with it, so the build uses the resolved versions rather than re-solving the constraints.
COPY web/pubspec.yaml web/pubspec.lock ./

# --enforce-lockfile, so a pubspec.yaml edited without regenerating pubspec.lock fails the build
# instead of quietly re-solving the constraints. The point of committing the lock file is that the
# image is built from the versions that were tested; silently resolving something else defeats it.
RUN flutter pub get --enforce-lockfile

COPY web/ ./

# --release, so the output is compiled and tree-shaken rather than the debug bundle.
RUN flutter build web --release

# ---------------------------------------------------------------------------------------------
# Stage 2: the runtime image.
# ---------------------------------------------------------------------------------------------
FROM nginx:1.27-alpine AS runtime

# Only the built bundle is carried across — none of the SDK, and none of the Dart source. The
# location block serving this is in default.conf, which is still mounted from the host at run time
# so a routing change needs no rebuild.
COPY --from=webbuild /src/build/web /usr/share/nginx/html

# Compiled JS and the CanvasKit payload are large and highly compressible, and this is the only
# thing between them and the browser. See the gzip block in default.conf for what is compressed
# and why the wasm binaries are included.
