namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// Builds the fixed --appName/--appId/--update-version/--update script for everything the Mac App
/// Store manages, the App Store's own row included. Shared between the "Find Upgrade Paths" research
/// flow (<see cref="Commands.ResearchApplicationUpgradePath.ResearchApplicationUpgradePathCommandHandler"/>)
/// and registration-time seeding (<c>RegisterApplicationsCommandHandler</c>) so both only ever
/// produce this one script shape under the <see cref="PlatformBucket.ForPackageManager"/> bucket —
/// never AI-generated. That last point is the reason this builder exists at all: an App Store bundle
/// reported as a standalone application went to the AI, whose macOS prompt assumes a Developer-ID
/// distribution and writes a script that downloads the vendor's DMG and replaces the bundle in
/// place. Signed and approved, that swaps an App Store build for a direct-download one — receipt,
/// sandbox container and in-app purchases gone, the store no longer recognizing it — and nothing
/// errors. The macOS agent now routes any bundle carrying <c>Contents/_MASReceipt/receipt</c> here
/// instead (see its <c>system_info::read_app_bundle</c>).
/// </summary>
/// <remarks>
/// <para>
/// The application is never baked into the returned text — both the server (via
/// <c>IUpgradePathResearchClient.CheckScriptVersionAsync</c>) and the macOS agent (via
/// <c>patch_one</c>) invoke every script with <c>--appName</c>/<c>--appId</c> as real arguments, so
/// the script reads them at runtime. <see cref="Build"/> therefore returns byte-identical content
/// for every App Store application and one human review covers all of them — see
/// <c>IUpgradePathRepository.FindExistingSignatureForScriptAsync</c>.
/// </para>
/// <para>
/// One text for the App Store's own row and every application it manages (see
/// <see cref="RecognizedPackageManager.BuildScript"/>), told apart at runtime by <c>--appName</c>
/// being the name the agent reports the store under (<c>system_info::APP_STORE_NAME</c>). The store
/// is part of macOS and updates with it, so — like <see cref="FlatpakUpgradeScript"/>'s own row —
/// that branch declines to answer a version, leaving <c>LatestVersion</c> null so the row never
/// patches; <c>softwareupdate</c> covers it through the agent's <c>os_update</c>.
/// </para>
/// <para>
/// The App Store qualifies for <see cref="PackageManagerCatalog"/> because Apple's iTunes Search
/// API answers "the latest version of this bundle identifier" over plain HTTP from anywhere, which
/// is the catalog's entry requirement (<c>--update-version</c> runs on the API server). Two things
/// about that API are silent traps and are written into the script rather than only here: the entity
/// has to be <c>desktopSoftware</c>, and the lookup is against one storefront. See the comments in
/// <see cref="Build"/>.
/// </para>
/// <para>
/// <c>--update</c> is deliberately not implemented yet. Since Apple's fix for CVE-2025-43411
/// (macOS 14.8.2 / 15.7.2 / 26.1) an App Store install needs root, while the download half needs the
/// logged-in user's store session — two users, neither of which the agent's per-user process is on
/// its own, and no Apple-supported command line does either. The branch exits non-zero saying so,
/// which is what a reviewer reading it before signing should see; a signed script that exited 0
/// having done nothing would make the agent report the latest version as installed. How that branch
/// gets filled in — Apple's own automatic updates, or a root-owned <c>mas</c> behind a sudoers rule
/// — is a deployment decision recorded in CLAUDE.md under "Platform buckets".
/// </para>
/// </remarks>
public static class AppStoreUpgradeScript
{
    public static string Build()
    {
        return """
            #!/bin/bash
            set -euo pipefail

            usage() {
              echo "Usage: $0 --appName <name> --appId <id> (--update-version|--update)" >&2
              exit 1
            }

            APP_NAME=""
            APP_ID=""
            MODE=""
            while [ $# -gt 0 ]; do
              case "$1" in
                --appName) APP_NAME="$2"; shift 2 ;;
                --appId) APP_ID="$2"; shift 2 ;;
                --update-version) [ -n "$MODE" ] && usage; MODE="update-version"; shift ;;
                --update) [ -n "$MODE" ] && usage; MODE="update"; shift ;;
                *) usage ;;
              esac
            done
            [ -n "$APP_NAME" ] || usage
            [ -n "$APP_ID" ] || usage
            [ -n "$MODE" ] || usage

            # The App Store's own row. The macOS agent reports the store itself as an application
            # named "App Store" (see system_info::APP_STORE_NAME), and it is not an App Store app: it
            # ships with macOS and is updated by softwareupdate along with the rest of the OS, which
            # the agent already handles as an OS update. One script serves it and everything it
            # manages so that one review covers all of them; this is where the two part ways.
            is_app_store_itself() {
              [ "$(printf '%s' "$APP_NAME" | tr '[:upper:]' '[:lower:]')" = "app store" ]
            }

            latest_version() {
              local response version

              if is_app_store_itself; then
                # Declining leaves LatestVersion null, so updateAvailable is false and the agent never
                # tries this row — the same reasoning as Flatpak's own row in FlatpakUpgradeScript.
                echo "the App Store is part of macOS and updates with it; nothing to report" >&2
                return 1
              fi

              # --appId is the bundle identifier the agent read from Info.plist. Two things about this
              # URL were each learned by getting a wrong answer that looked right:
              #
              # - entity=desktopSoftware, NOT entity=macSoftware. For an app sold as one purchase on
              #   iOS and macOS (Pages, Keynote, Numbers, ...) macSoftware returns the iOS record and
              #   its version — 15.3 when the Mac build was 15.3.1 — so the row would compare against
              #   the wrong platform's release. desktopSoftware is what `mas` itself queries.
              # - No country= parameter means the US storefront. An app not sold there answers
              #   resultCount 0, the version stays null, and the row never patches — the same silent
              #   failure as any other null LatestVersion. The server cannot know a host's storefront,
              #   so this is documented rather than solved.
              #
              # The API is eventually consistent (hours to days behind the store) and rate-limited to
              # roughly twenty calls a minute; a failed check keeps the row's previous LatestVersion
              # (CheckApplicationUpdateCommandHandler), so a throttled run only delays freshness.
              response=$(curl -fsSL "https://itunes.apple.com/lookup?bundleId=${APP_ID}&entity=desktopSoftware") || return 1

              # A miss is a 200 with "resultCount":0 and an empty results array, so the count is
              # checked before anything is read out of it. Read with grep/sed because jq is not
              # something the API server is guaranteed to have.
              printf '%s' "$response" | grep -q '"resultCount":[[:space:]]*[1-9]' || return 1
              version=$(printf '%s' "$response" \
                | grep -o '"version"[[:space:]]*:[[:space:]]*"[^"]*"' \
                | head -1 \
                | sed -E 's/.*"([^"]*)"$/\1/')

              [ -n "$version" ] || return 1
              printf '%s' "$version"
            }

            if [ "$MODE" = "update-version" ]; then
              version=$(latest_version) || { echo "could not determine the latest version" >&2; exit 1; }
              printf '%s\n' "$version"
              exit 0
            fi

            # --update mode: runs on the managed Mac, as the logged-in user (the agent's per-user
            # process runs every package-manager row, see upgrade::runs_as_root).
            #
            # Not implemented yet, and it says so rather than pretending. Since Apple's fix for
            # CVE-2025-43411 (macOS 14.8.2 / 15.7.2 / 26.1) installing an App Store update needs root,
            # while starting the download needs the logged-in user's store session; the per-user
            # process is one of those and cannot become the other, and Apple ships no command line
            # for either. Exiting non-zero here is load-bearing: an exit 0 that did nothing would make
            # the agent report the latest version as installed (patch_cycle::run_patches) and the
            # next inventory would contradict it. Do not sign this script expecting it to patch.
            echo "App Store updates are not yet performed by the agent; the App Store's own automatic" >&2
            echo "updates (System Settings > App Store > Automatic Updates) install them." >&2
            exit 1
            """;
    }
}
