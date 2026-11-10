#!/bin/bash
# Builds NOX.app for Apple Silicon directly on a Mac (needs the .NET 10 SDK) and signs it ad hoc.
# Usage, from the repository root:  bash Zkt/Client.MacOS/macOS/build-on-mac.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
PROJECT="$ROOT/Zkt/Client.MacOS/Client.MacOS.csproj"
OUT="$ROOT/publish/NOX-macOS-arm64"
APP="$OUT/NOX.app"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | head -1)"

rm -rf "$OUT"
dotnet publish "$PROJECT" -c Release -r osx-arm64 --self-contained true -o "$OUT/bin"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$OUT/bin/." "$APP/Contents/MacOS/"
cp "$ROOT/Zkt/Client.MacOS/Assets/NOX.icns" "$APP/Contents/Resources/NOX.icns"
sed "s/__VERSION__/$VERSION/g" "$ROOT/Zkt/Client.MacOS/macOS/Info.plist" > "$APP/Contents/Info.plist"
rm -rf "$OUT/bin"
chmod +x "$APP/Contents/MacOS/NOX"

codesign --force --deep --sign - "$APP"
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true

# Zip + SHA-256 with the fixed names expected by install-nox.sh (ditto keeps Unix permissions).
ZIP="NOX-macOS-arm64.zip"
(cd "$OUT" && ditto -c -k --keepParent NOX.app "$ZIP" && shasum -a 256 "$ZIP" > "$ZIP.sha256")
cp "$ROOT/Zkt/Client.MacOS/macOS/install-nox.sh" "$OUT/install-nox.sh"
echo "Pronto: $APP"
echo "Da allegare alla release GitHub (versione $VERSION): $ZIP, $ZIP.sha256, install-nox.sh in $OUT"
open "$OUT"
