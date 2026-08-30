namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// Builds the fixed --appName/--appId/--update-version/--update script for a snap. Shared between
/// the "Find Upgrade Paths" research flow
/// (<see cref="Commands.ResearchApplicationUpgradePath.ResearchApplicationUpgradePathCommandHandler"/>)
/// and registration-time seeding (<c>RegisterApplicationsCommandHandler</c>) so both only ever
/// produce this one script shape — never AI-generated, since snapd's upgrade mechanics are already
/// fully known and there's nothing to research.
/// </summary>
/// <remarks>
/// <para>
/// Unlike every other manager in <see cref="PackageManagerCatalog"/>, <paramref name="isSelfUpdate"/>
/// changes nothing here, and the parameter is accepted only to satisfy
/// <see cref="RecognizedPackageManager.BuildScript"/>'s shared signature. Homebrew's own row needs a
/// different script because Homebrew is not a formula, and Flatpak's because Flatpak is a
/// distribution package — but snapd genuinely *is* a snap, published on the same store under the
/// name "snapd", so `snap refresh snapd` is the same operation as `snap refresh firefox` with a
/// different <c>--appId</c>. Returning one script for both cases isn't a shortcut; it means the two
/// rows share a signature and one human review covers both (see
/// <c>IUpgradePathRepository.FindExistingSignatureForScriptAsync</c>).
/// </para>
/// <para>
/// The snap is never baked into the returned text — <c>--appId</c> carries it at runtime — so
/// <see cref="Build"/> returns byte-identical content for every snap on every host.
/// </para>
/// <para>
/// Snap qualifies for the catalog because the Snap Store answers "what is the latest version of
/// this snap" over plain HTTP, which is what lets <c>--update-version</c> run on the API server
/// rather than the managed host. One caveat comes with that: the store publishes a version per
/// architecture, and this reads the first stable entry, so a mixed-architecture fleet can be told a
/// version that belongs to a different architecture than a given host's. The consequence is bounded
/// — <c>snap refresh</c> is idempotent and exits 0 when there is nothing to do — but it is the
/// reason a per-host package manager like apt could never be handled this way at all.
/// </para>
/// </remarks>
public static class SnapUpgradeScript
{
    public static string Build(bool isSelfUpdate)
    {
        _ = isSelfUpdate; // Intentionally unused — see the remarks above.

        return """
            #!/bin/bash
            set -euo pipefail

            usage() {
              echo "Usage: $0 --appName <name> --appId <id> (--update-version|--update)" >&2
              exit 1
            }

            APP_ID=""
            MODE=""
            while [ $# -gt 0 ]; do
              case "$1" in
                --appName) shift 2 ;;
                --appId) APP_ID="$2"; shift 2 ;;
                --update-version) [ -n "$MODE" ] && usage; MODE="update-version"; shift ;;
                --update) [ -n "$MODE" ] && usage; MODE="update"; shift ;;
                *) usage ;;
              esac
            done
            [ -n "$APP_ID" ] || usage
            [ -n "$MODE" ] || usage

            latest_version() {
              local response
              # The Snap-Device-Series header is required; the store rejects the request without it.
              response=$(curl -fsSL -H 'Snap-Device-Series: 16' \
                "https://api.snapcraft.io/v2/snaps/info/${APP_ID}?fields=version") || return 1

              # channel-map is ordered stable-first, so the first "version" is the current stable
              # release. Read with grep/sed rather than jq, which the API server is not guaranteed
              # to have.
              local version
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

            # --update mode: runs on the managed host itself, as root from a systemd service, whose
            # PATH does not include /snap/bin on any distribution by default.
            export PATH="$PATH:/snap/bin"
            if ! command -v snap >/dev/null 2>&1; then
              echo "snapd is not installed" >&2
              exit 1
            fi

            # Idempotent: exits 0 with "has no updates available" when already current.
            snap refresh "$APP_ID"
            """;
    }
}
