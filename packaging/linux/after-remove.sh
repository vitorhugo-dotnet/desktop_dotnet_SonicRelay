#!/bin/sh
# Best-effort desktop/icon cache refresh after removal. Never fails the package
# transaction: both tools are optional and only present on desktop systems.
set -e

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q /usr/share/applications || true
fi

if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -f /usr/share/icons/hicolor || true
fi
