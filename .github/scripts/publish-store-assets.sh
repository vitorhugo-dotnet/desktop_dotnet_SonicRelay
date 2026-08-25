#!/usr/bin/env bash
# Adds the Microsoft Store MSIX to an existing GitHub Release and folds its checksums into
# the release's single canonical checksums file.
#
# Shared by .github/workflows/ci.yml and .github/workflows/release.yml so the two cannot
# drift, and so both go through retry_gh: this runs at the end of a long, expensive build,
# and a transient api.github.com 5xx must not leave the Store package reachable only from
# the workflow run.
#
# The package is deliberately unsigned - the Store re-signs everything it ingests and
# rejects a package signed by anybody else - so the notes written here say plainly that it
# is the Partner Center upload, not an installer.
#
# Required environment:
#   GH_TOKEN, RELEASE_TAG, RELEASE_VERSION, STORE_VERSION
set -euo pipefail

: "${RELEASE_TAG:?RELEASE_TAG is required}"
: "${RELEASE_VERSION:?RELEASE_VERSION is required}"
: "${STORE_VERSION:?STORE_VERSION is required}"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
dist_dir="${STORE_DIST_DIR:-artifacts/store}"

# shellcheck source=.github/scripts/gh-retry.sh
source "$repo_root/.github/scripts/gh-retry.sh"

shopt -s nullglob
packages=()
for package in "$dist_dir"/*.msix "$dist_dir"/*.msixupload; do
    packages+=("$(basename "$package")")
done
shopt -u nullglob

if (( ${#packages[@]} == 0 )); then
    echo "::error::No .msix or .msixupload package was found in $dist_dir." >&2
    exit 1
fi

# Merge into the checksums file the Windows job created, so there is one canonical
# checksums file per release covering every asset.
retry_gh gh release download "$RELEASE_TAG" --pattern checksums-sha256.txt --dir "$dist_dir" --clobber
( cd "$dist_dir" && sha256sum "${packages[@]}" >> checksums-sha256.txt )

storeNotes=$'\n\n## Microsoft Store package\n\n'
for package in "${packages[@]}"; do
    case "$package" in
        *.msixupload)
            storeNotes+="- \`$package\`: what the Partner Center **Packages** page expects (the \`.msix\` plus its \`.appxsym\` symbols, so crash analytics can symbolicate).
"
            ;;
        *)
            storeNotes+="- \`$package\`: the x64 Store package itself.
"
            ;;
    esac
done
storeNotes+=$(printf '\nBoth are **unsigned on purpose**: the Microsoft Store re-signs every package it ingests and rejects one signed by anybody else, so Windows refuses to install this `.msix` by double-clicking it. Install SonicRelay from the `.msi` or `.exe` above, or from the Store once the submission is live; these two assets exist so a submission can be reproduced from the release instead of from an expiring workflow artifact.\n\nPackage version `%s` follows the Store rule that the version is `Major.Minor.Build.0` with a non-zero part, so it can differ from the release version `%s`. See `docs/microsoft-store-package.md`.\n' "$STORE_VERSION" "$RELEASE_VERSION")

currentNotes=$(retry_gh gh release view "$RELEASE_TAG" --json body --jq .body)
retry_gh gh release edit "$RELEASE_TAG" --notes "${currentNotes}${storeNotes}"

uploads=()
for package in "${packages[@]}"; do
    uploads+=("$dist_dir/$package")
done

retry_gh gh release upload "$RELEASE_TAG" "${uploads[@]}" "$dist_dir/checksums-sha256.txt" --clobber
