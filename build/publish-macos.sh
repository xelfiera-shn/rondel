#!/usr/bin/env bash
# Builds Rondel.app (NativeAOT) into dist/<runtime>/ and signs it.
#
#   build/publish-macos.sh [--runtime osx-arm64|osx-x64] [--version 0.1.0] [--identity "Rondel Development"] [--sign-only]
#
# --sign-only signs the app already built there, without building: the release job builds with --identity - (ad hoc)
# and imports the release certificate only afterwards, so no package's build code runs while it is in reach.
#
# macOS ties the Accessibility and Automation permissions to the app's signature, so every build is signed with
# the same certificate; an ad-hoc signature changes with every build and the permissions would have to be granted
# again. For development, create a self-signed one once: Keychain Access > Certificate Assistant > Create a
# Certificate, name "Rondel Development", identity type Self-Signed Root, certificate type Code Signing.
set -euo pipefail

runtime="osx-$(uname -m | sed 's/x86_64/x64/')"
version="0.0.0"
identity="Rondel Development"
sign_only=false
while [ $# -gt 0 ]; do
    case "$1" in
        --runtime) runtime="$2"; shift 2 ;;
        --version) version="$2"; shift 2 ;;
        --identity) identity="$2"; shift 2 ;;
        --sign-only) sign_only=true; shift ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

root="$(cd "$(dirname "$0")/.." && pwd)"
app="$root/dist/$runtime/Rondel.app"
export AVALONIA_TELEMETRY_OPTOUT=1

if [ "$sign_only" = false ]; then
echo "Publishing Rondel.app $version for $runtime ..."
rm -rf "$app"
dotnet publish "$root/src/Rondel.MacOS/Rondel.MacOS.csproj" -c Release -r "$runtime" -o "$app/Contents/MacOS" \
    -p:Version="$version" -p:PublishAot=true -p:DebugType=none -p:StripSymbols=true -nologo -v quiet
rm -rf "$app/Contents/MacOS/"*.dSYM # debug symbols are not shipped
mkdir -p "$app/Contents/Resources"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>app.rondel</string>
  <key>CFBundleName</key><string>Rondel</string>
  <key>CFBundleDisplayName</key><string>Rondel</string>
  <key>CFBundleExecutable</key><string>Rondel</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>15.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSAppleEventsUsageDescription</key><string>Rondel asks Finder which folder a window shows, to open its menu there.</string>
</dict>
</plist>
PLIST
elif [ ! -d "$app" ]; then
    echo "No $app to sign; build it first" >&2
    exit 1
fi

# The libraries first, then the app: a bundle is signed from the inside out.
find "$app/Contents/MacOS" -name '*.dylib' -exec codesign --force --timestamp=none --sign "$identity" {} \;
codesign --force --timestamp=none --sign "$identity" "$app"
codesign --verify --strict "$app"
echo "  $app ($(du -sh "$app" | cut -f1)), signed with \"$identity\""
