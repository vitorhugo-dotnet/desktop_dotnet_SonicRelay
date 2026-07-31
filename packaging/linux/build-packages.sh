#!/usr/bin/env bash
# Builds the Linux release assets for the SonicRelay publisher (issue #40) from an
# already-published self-contained linux-x64 output: a portable .tar.gz, a Debian/Ubuntu
# .deb, and a Fedora .rpm, all sharing the same FHS staging layout and version.
#
# Usage: build-packages.sh <publish-dir> <version> <commit-sha> <output-dir>
set -euo pipefail

if [ "$#" -ne 4 ]; then
    echo "usage: $0 <publish-dir> <version> <commit-sha> <output-dir>" >&2
    exit 1
fi

publish_dir=$1
version=$2
commit_sha=$3
output_dir=$4

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
product_name="SonicRelay-LinuxPublisher"
runtime_id="linux-x64"
app_binary="SonicRelay.Windows.Desktop"

mkdir -p "$output_dir"

cat > "$publish_dir/BUILD-INFO.txt" <<EOF
product=SonicRelay Linux Publisher
version=$version
commit=$commit_sha
runtime=$runtime_id
configuration=Release
builtAtUtc=$(date -u +%Y-%m-%dT%H:%M:%SZ)
EOF

chmod +x "$publish_dir/$app_binary"

# ---- Portable archive -------------------------------------------------------
tarball="$output_dir/$product_name-$runtime_id-$version.tar.gz"
tar -czf "$tarball" -C "$publish_dir" .
echo "Wrote $tarball"

# ---- FHS staging shared by .deb and .rpm ------------------------------------
staging_dir="$(mktemp -d)"
trap 'rm -rf "$staging_dir"' EXIT

install -d "$staging_dir/usr/lib/sonicrelay"
cp -a "$publish_dir/." "$staging_dir/usr/lib/sonicrelay/"

install -d "$staging_dir/usr/bin"
install -m 755 "$repo_root/packaging/linux/sonicrelay" "$staging_dir/usr/bin/sonicrelay"

install -d "$staging_dir/usr/share/applications"
install -m 644 "$repo_root/packaging/linux/sonicrelay.desktop" "$staging_dir/usr/share/applications/sonicrelay.desktop"

install -d "$staging_dir/usr/share/icons/hicolor/256x256/apps"
install -m 644 "$repo_root/packaging/linux/icons/sonicrelay.png" "$staging_dir/usr/share/icons/hicolor/256x256/apps/sonicrelay.png"

install -d "$staging_dir/usr/share/icons/hicolor/scalable/apps"
install -m 644 "$repo_root/packaging/linux/icons/sonicrelay.svg" "$staging_dir/usr/share/icons/hicolor/scalable/apps/sonicrelay.svg"

# rpm forbids '-' in Version/Release, and Debian discourages it outside the
# upstream/revision split; split "<numeric>[-+]<suffix>" into a clean --version and a
# dot-only --iteration so alpha/manual dev versions (e.g. 0.0.0-alpha.pr5.42) still
# produce valid packages alongside plain releases (e.g. 0.1.0).
package_version="${version%%[+-]*}"
[ -n "$package_version" ] || package_version="0.0.0"

suffix="${version#"$package_version"}"
suffix="${suffix#[-+]}"

if [ -n "$suffix" ]; then
    iteration="$(printf '%s' "$suffix" | tr -c 'A-Za-z0-9.' '.' | sed -e 's/\.\.*/./g' -e 's/^\.//' -e 's/\.$//')"
    [ -n "$iteration" ] || iteration="1"
else
    iteration="1"
fi

common_args=(
    --input-type dir
    --name sonicrelay
    --version "$package_version"
    --iteration "$iteration"
    --license "See LICENSE"
    --vendor SonicRelay
    --maintainer "SonicRelay <noreply@sonicrelay.invalid>"
    --description "SonicRelay Linux Publisher: capture desktop audio and stream it to SonicRelay viewers."
    --url "https://github.com/vitorhugo-dotnet/windows_SonicRelay"
    --after-install "$repo_root/packaging/linux/after-install.sh"
    --after-remove "$repo_root/packaging/linux/after-remove.sh"
    --chdir "$staging_dir"
    usr
)

# ---- .deb (Ubuntu 24.04 / Debian) --------------------------------------------
# Runtime dependency names match Microsoft's documented Ubuntu 24.04 (Noble) list
# for a manually published/self-contained .NET app, plus Avalonia's X11/fontconfig
# native libraries.
debPath="$output_dir/$product_name-$runtime_id-$version.deb"
fpm --output-type deb \
    "${common_args[@]}" \
    --depends ca-certificates --depends libc6 --depends libgcc-s1 \
    --depends libgssapi-krb5-2 --depends libicu74 --depends libssl3t64 \
    --depends libstdc++6 --depends tzdata --depends zlib1g \
    --depends libfontconfig1 --depends libx11-6 \
    --package "$debPath"
echo "Wrote $debPath"

# ---- .rpm (Fedora, best effort) ------------------------------------------------
# Runtime dependency names match Microsoft's documented Fedora list for a
# manually published/self-contained .NET app, plus Avalonia's X11/fontconfig
# native libraries.
rpmPath="$output_dir/$product_name-$runtime_id-$version.rpm"
fpm --output-type rpm \
    "${common_args[@]}" \
    --depends glibc --depends libgcc --depends ca-certificates \
    --depends openssl-libs --depends libstdc++ --depends libicu \
    --depends tzdata --depends krb5-libs --depends zlib \
    --depends fontconfig --depends libX11 \
    --package "$rpmPath"
echo "Wrote $rpmPath"
