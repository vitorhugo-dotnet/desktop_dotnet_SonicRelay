#!/usr/bin/env bash
# Compiles the SonicRelay macOS system-audio tap helper (issue #62) from
# src/SonicRelay.Platform.MacOs/native/SonicRelayAudioTap.swift.
#
# The helper is native Swift against ScreenCaptureKit, so this only runs on a
# macOS host with the Xcode command line tools — which is exactly why the .NET
# project treats it as a runtime dependency rather than an MSBuild input: the
# solution still builds on Windows and Linux agents.
#
# Usage: build-audio-tap.sh <output-path> [arch...]
#   arch defaults to the host architecture; pass "arm64 x86_64" for a universal
#   helper.
set -euo pipefail

if [ "$#" -lt 1 ]; then
    echo "usage: $0 <output-path> [arch...]" >&2
    exit 1
fi

output_path=$1
shift

if [ "$#" -gt 0 ]; then
    architectures=("$@")
else
    architectures=("$(uname -m)")
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source_path="$repo_root/src/SonicRelay.Platform.MacOs/native/SonicRelayAudioTap.swift"

if [ ! -f "$source_path" ]; then
    echo "error: helper source not found at $source_path" >&2
    exit 1
fi

mkdir -p "$(dirname "$output_path")"

work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

# swiftc emits one architecture per invocation, so a universal helper is built
# by compiling each slice and merging them with lipo — the same way the app
# bundle's universal variant is produced.
slices=()
for arch in "${architectures[@]}"; do
    slice="$work_dir/sonicrelay-audio-tap.$arch"
    # -O for a release build; the helper sits on the audio path for the whole
    # session. The deployment target must match Info.plist's
    # LSMinimumSystemVersion: SCStream audio capture is macOS 13+.
    xcrun swiftc \
        -O \
        -target "${arch}-apple-macos13.0" \
        -framework ScreenCaptureKit \
        -framework AVFoundation \
        -framework CoreMedia \
        -framework CoreGraphics \
        -o "$slice" \
        "$source_path"
    slices+=("$slice")
done

if [ "${#slices[@]}" -eq 1 ]; then
    cp "${slices[0]}" "$output_path"
else
    xcrun lipo -create "${slices[@]}" -output "$output_path"
fi

chmod +x "$output_path"
echo "Wrote $output_path"

# Smoke-check the binary only when a slice matches the host, so a
# cross-compiled artifact is not reported as broken just because it cannot run
# here.
for arch in "${architectures[@]}"; do
    if [ "$arch" = "$(uname -m)" ]; then
        "$output_path" --version
        break
    fi
done
