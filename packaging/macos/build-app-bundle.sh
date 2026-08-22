#!/usr/bin/env bash
# Builds the SonicRelay macOS release assets (issue #62) from an already-published
# self-contained .NET output: a SonicRelay.app bundle containing the Avalonia shell
# and the native ScreenCaptureKit tap helper, packaged as a .zip and a .dmg.
#
# Code signing and notarization are applied only when Apple Developer credentials
# are present in the environment; without them the same bundle is produced
# unsigned, so pull requests and forks still get a testable artifact. Which of the
# two happened is recorded in BUILD-INFO.txt and echoed here.
#
# Usage: build-app-bundle.sh <publish-dir> <runtime-id> <version> <commit-sha> <output-dir>
#
# Optional environment:
#   MACOS_SIGN_IDENTITY     Developer ID Application identity (enables signing)
#   MACOS_NOTARY_APPLE_ID   Apple ID for notarytool (enables notarization)
#   MACOS_NOTARY_TEAM_ID    Developer Team ID
#   MACOS_NOTARY_PASSWORD   App-specific password for the Apple ID
set -euo pipefail

if [ "$#" -ne 5 ]; then
    echo "usage: $0 <publish-dir> <runtime-id> <version> <commit-sha> <output-dir>" >&2
    exit 1
fi

publish_dir=$1
runtime_id=$2
version=$3
commit_sha=$4
output_dir=$5

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
product_name="SonicRelay-MacPublisher"
app_binary="SonicRelay.Windows.Desktop"
bundle_name="SonicRelay.app"

case "$runtime_id" in
    osx-arm64) helper_architectures=(arm64) ;;
    osx-x64)   helper_architectures=(x86_64) ;;
    *) echo "error: unsupported runtime id '$runtime_id' (expected osx-arm64 or osx-x64)" >&2; exit 1 ;;
esac

mkdir -p "$output_dir"
staging_dir="$(mktemp -d)"
trap 'rm -rf "$staging_dir"' EXIT

app_dir="$staging_dir/$bundle_name"
macos_dir="$app_dir/Contents/MacOS"
resources_dir="$app_dir/Contents/Resources"
install -d "$macos_dir" "$resources_dir"

# ---- Application payload -----------------------------------------------------
cp -a "$publish_dir/." "$macos_dir/"
chmod +x "$macos_dir/$app_binary"

# ---- Native system-audio helper ---------------------------------------------
# Lives beside the app binary inside the bundle, which is what AudioTapLocator
# resolves. That placement is a privacy requirement, not a convention: macOS
# grants Screen Recording consent to a signed bundle identity, so the helper
# only inherits SonicRelay's grant while it is part of SonicRelay.app.
"$repo_root/packaging/macos/build-audio-tap.sh" "$macos_dir/sonicrelay-audio-tap" "${helper_architectures[@]}"

# ---- Bundle metadata ---------------------------------------------------------
# CFBundleShortVersionString must be a plain dotted number; a prerelease
# version like 0.0.0-alpha.pr5.42 keeps its full form in BUILD-INFO.txt while
# the plist carries the numeric prefix macOS will accept.
plist_version="${version%%[+-]*}"
[ -n "$plist_version" ] || plist_version="0.0.0"
sed "s/__VERSION__/$plist_version/g" "$repo_root/packaging/macos/Info.plist" > "$app_dir/Contents/Info.plist"
printf 'APPL????' > "$app_dir/Contents/PkgInfo"

# The app icon is generated from the same source artwork the Linux packages use.
iconset_dir="$staging_dir/SonicRelay.iconset"
mkdir -p "$iconset_dir"
source_icon="$repo_root/packaging/linux/icons/sonicrelay.png"
for size in 16 32 64 128 256 512; do
    sips -z "$size" "$size" "$source_icon" --out "$iconset_dir/icon_${size}x${size}.png" >/dev/null
    sips -z "$((size * 2))" "$((size * 2))" "$source_icon" --out "$iconset_dir/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil --convert icns "$iconset_dir" --output "$resources_dir/SonicRelay.icns"

cat > "$macos_dir/BUILD-INFO.txt" <<EOF
product=SonicRelay macOS Publisher
version=$version
commit=$commit_sha
runtime=$runtime_id
configuration=Release
builtAtUtc=$(date -u +%Y-%m-%dT%H:%M:%SZ)
EOF

# ---- Signing -----------------------------------------------------------------
signing_state="unsigned"
if [ -n "${MACOS_SIGN_IDENTITY:-}" ]; then
    entitlements="$repo_root/packaging/macos/SonicRelay.entitlements"

    # Sign inside out: macOS requires nested code to be sealed before the
    # bundle that contains it, otherwise the outer signature is invalidated the
    # moment an inner binary is re-signed. --deep is deliberately not used;
    # Apple documents it as unsuitable for signing a distribution build,
    # because it applies the app's entitlements to every nested binary.
    while IFS= read -r nested; do
        codesign --force --timestamp --options runtime \
            --sign "$MACOS_SIGN_IDENTITY" "$nested"
    done < <(find "$macos_dir" -type f \( -name '*.dylib' -o -name 'sonicrelay-audio-tap' \))

    codesign --force --timestamp --options runtime \
        --entitlements "$entitlements" \
        --sign "$MACOS_SIGN_IDENTITY" "$app_dir"

    codesign --verify --strict --verbose=2 "$app_dir"
    signing_state="signed"
    echo "Signed $bundle_name with $MACOS_SIGN_IDENTITY"
else
    echo "MACOS_SIGN_IDENTITY is not set: producing an unsigned bundle."
fi

# ---- Notarization ------------------------------------------------------------
# Only meaningful for a signed bundle: the notary service rejects unsigned
# submissions, and Gatekeeper needs both the Developer ID signature and the
# stapled ticket before it will run a downloaded app without a warning.
if [ "$signing_state" = "signed" ] && [ -n "${MACOS_NOTARY_APPLE_ID:-}" ] && [ -n "${MACOS_NOTARY_TEAM_ID:-}" ] && [ -n "${MACOS_NOTARY_PASSWORD:-}" ]; then
    notarize_zip="$staging_dir/notarize.zip"
    ditto -c -k --keepParent "$app_dir" "$notarize_zip"
    xcrun notarytool submit "$notarize_zip" \
        --apple-id "$MACOS_NOTARY_APPLE_ID" \
        --team-id "$MACOS_NOTARY_TEAM_ID" \
        --password "$MACOS_NOTARY_PASSWORD" \
        --wait
    # Stapling writes the ticket into the bundle, so both assets below must be
    # built afterwards for offline Gatekeeper checks to succeed.
    xcrun stapler staple "$app_dir"
    signing_state="signed+notarized"
    echo "Notarized and stapled $bundle_name"
elif [ "$signing_state" = "signed" ]; then
    echo "Apple notary credentials are not set: the bundle is signed but not notarized."
fi

echo "signing=$signing_state" >> "$macos_dir/BUILD-INFO.txt"

# ---- Assets ------------------------------------------------------------------
# ditto (not zip) preserves symlinks, resource forks, and the signature's
# extended attributes; a plain zip round-trip breaks the code signature.
zip_path="$output_dir/$product_name-$runtime_id-$version.zip"
ditto -c -k --keepParent "$app_dir" "$zip_path"
echo "Wrote $zip_path"

dmg_path="$output_dir/$product_name-$runtime_id-$version.dmg"
dmg_root="$staging_dir/dmg"
install -d "$dmg_root"
cp -a "$app_dir" "$dmg_root/$bundle_name"
# The Applications symlink is what makes the window a drag-to-install target.
ln -s /Applications "$dmg_root/Applications"
hdiutil create \
    -volname "SonicRelay $version" \
    -srcfolder "$dmg_root" \
    -ov -format UDZO \
    "$dmg_path" >/dev/null
echo "Wrote $dmg_path"
