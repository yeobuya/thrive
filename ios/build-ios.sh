#!/bin/bash
# Builds Thrive for iPhone on this Mac and uploads it to TestFlight.
#   bash ios/build-ios.sh                # native libs, Godot export, archive, upload
#   bash ios/build-ios.sh --no-upload    # stop after the archive (first run registers the app ID)
#   bash ios/build-ios.sh --skip-native  # reuse lib/ios/thrive_ios.xcframework from an earlier run
#   bash ios/build-ios.sh --upload-only  # upload the last archive again
# Options: --team (default M5W572B65D, or APPLE_TEAM_ID), --bundle-id (default com.yeobuya.thrive),
#          --godot /path/to/Godot (default: GODOT env var, then /Applications/Godot_mono.app).
# Needs: Xcode signed in to your Apple developer account, Godot 4.7.2 .NET (mono) editor with its export
# templates installed, the .NET 10 SDK, CMake and git-lfs. Full steps: doc/ios_port.md.
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT=$PWD
TEAM="${APPLE_TEAM_ID:-M5W572B65D}"
BUNDLE_ID="com.yeobuya.thrive"
GODOT_VERSION="4.7.2"
GODOT="${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"
UPLOAD=1
BUILD=1
NATIVE=1
MIN_IOS="16.0"
while [ $# -gt 0 ]; do
  case "$1" in
    --team) TEAM="$2"; shift ;;
    --bundle-id) BUNDLE_ID="$2"; shift ;;
    --godot) GODOT="$2"; shift ;;
    --no-upload) UPLOAD=0 ;;
    --upload-only) BUILD=0 ;;
    --skip-native) NATIVE=0 ;;
    *) echo "Unknown option: $1"; exit 2 ;;
  esac
  shift
done

OUT="$ROOT/builds/ios"
XCODE_DIR="$OUT/xcode"
ARCHIVE="$OUT/Thrive.xcarchive"
NATIVE_BUILD="$ROOT/build-ios"
XCFRAMEWORK="$ROOT/lib/ios/thrive_ios.xcframework"
mkdir -p "$OUT"
# Keep Godot from importing build output as game resources
touch "$ROOT/builds/.gdignore"

need() { command -v "$1" >/dev/null || { echo "$1 not found. $2"; exit 1; }; }

if [ "$BUILD" = 1 ]; then
  need xcodebuild "Install Xcode from the App Store, open it once, then run: sudo xcode-select -s /Applications/Xcode.app"
  need cmake "Install it with: brew install cmake"
  need dotnet "Install the .NET 10 SDK from https://dotnet.microsoft.com/download"
  need git-lfs "Install it with: brew install git-lfs && git lfs install"
  [ -x "$GODOT" ] || { echo "Godot not found at $GODOT. Install Godot $GODOT_VERSION (.NET) or pass --godot."; exit 1; }
  "$GODOT" --version | grep -q "^$GODOT_VERSION.stable.mono" \
    || { echo "Thrive needs Godot $GODOT_VERSION .NET, found: $("$GODOT" --version)"; exit 1; }
  TEMPLATES="$HOME/Library/Application Support/Godot/export_templates/$GODOT_VERSION.stable.mono"
  [ -f "$TEMPLATES/ios.zip" ] \
    || { echo "No iOS export template at $TEMPLATES. In Godot: Editor > Manage Export Templates > Download and Install."; exit 1; }

  echo "Fetching submodules and art (Git LFS)..."
  git submodule update --init --recursive
  git lfs pull
  if head -c 40 assets/misc/icon.png | grep -q "git-lfs"; then
    echo "Art files are still Git LFS pointers, so the game would have no textures. Check 'git lfs pull' output."; exit 1
  fi

  if [ "$NATIVE" = 1 ]; then
    echo "Building the native libraries for iPhone (log: $OUT/native.log)..."
    rm -rf "$NATIVE_BUILD" "$XCFRAMEWORK"
    mkdir -p "$NATIVE_BUILD/api"
    touch "$NATIVE_BUILD/.gdignore"
    # godot-cpp is generated from the API of the exact Godot version in use
    (cd "$NATIVE_BUILD/api" && "$GODOT" --headless --dump-extension-api --dump-gdextension-interface >/dev/null 2>&1)
    [ -f "$NATIVE_BUILD/api/extension_api.json" ] || { echo "Godot didn't dump extension_api.json"; exit 1; }

    cmake -S "$ROOT" -B "$NATIVE_BUILD" -G "Unix Makefiles" \
      -DCMAKE_SYSTEM_NAME=iOS -DCMAKE_OSX_SYSROOT=iphoneos -DCMAKE_OSX_ARCHITECTURES=arm64 \
      -DCMAKE_OSX_DEPLOYMENT_TARGET="$MIN_IOS" -DCMAKE_BUILD_TYPE=Release \
      -DTHRIVE_AVX=OFF -DTHRIVE_LTO=OFF -DTHRIVE_LIBRARY_TYPE=STATIC -DWARNINGS_AS_ERRORS=OFF \
      -DTHRIVE_GODOT_API_FILE="$NATIVE_BUILD/api" > "$OUT/native.log" 2>&1 \
      || { tail -30 "$OUT/native.log"; echo "CMake configure failed. Full log: $OUT/native.log"; exit 1; }
    cmake --build "$NATIVE_BUILD" --target thrive_native thrive_extension -j "$(sysctl -n hw.ncpu)" \
      >> "$OUT/native.log" 2>&1 \
      || { grep -E 'error:' "$OUT/native.log" | head -20; echo "Native build failed. Full log: $OUT/native.log"; exit 1; }

    # Merge Thrive's libraries and their dependencies (Jolt, godot-cpp) into one static library so the app
    # links everything from a single place
    LIBS=$(find "$NATIVE_BUILD" -name '*.a' \( -name 'libthrive_*' -o -name 'libJolt*' -o -name 'libgodot-cpp*' \))
    echo "Merging: $LIBS" >> "$OUT/native.log"
    mkdir -p "$NATIVE_BUILD/merged"
    # shellcheck disable=SC2086
    libtool -static -o "$NATIVE_BUILD/merged/libthrive_ios.a" $LIBS
    xcodebuild -create-xcframework -library "$NATIVE_BUILD/merged/libthrive_ios.a" -output "$XCFRAMEWORK" \
      >> "$OUT/native.log" 2>&1
  fi
  [ -d "$XCFRAMEWORK" ] || { echo "No $XCFRAMEWORK. Run without --skip-native."; exit 1; }
  STATIC_LIB=$(find "$XCFRAMEWORK" -name 'libthrive_ios.a' | head -1)

  echo "Importing assets and building C# (first run takes a long time)..."
  "$GODOT" --headless --path "$ROOT" --import > "$OUT/import.log" 2>&1 || true
  "$GODOT" --headless --path "$ROOT" --build-solutions --quit > "$OUT/csharp.log" 2>&1 || true

  echo "Exporting the Xcode project from Godot (log: $OUT/export.log)..."
  rm -rf "$XCODE_DIR" "$ARCHIVE"
  mkdir -p "$XCODE_DIR"
  "$GODOT" --headless --path "$ROOT" --export-release "iOS" "$XCODE_DIR/Thrive.ipa" > "$OUT/export.log" 2>&1 || true
  XCODE_PROJ=$(find "$XCODE_DIR" -maxdepth 1 -name '*.xcodeproj' | head -1)
  if [ -z "$XCODE_PROJ" ]; then
    grep -iE 'error|failed' "$OUT/export.log" | head -20 || tail -40 "$OUT/export.log"
    echo "Godot didn't write an Xcode project. If the log mentions a missing iOS workload, run:"
    echo "  sudo dotnet workload install ios"
    exit 1
  fi
  SCHEME=$(basename "$XCODE_PROJ" .xcodeproj)
  PLIST=$(find "$XCODE_DIR" -maxdepth 2 -name "$SCHEME-Info.plist" | head -1)

  # TestFlight refuses a build number it has already seen; the commit count only goes up
  BUILD_NUMBER=$(git rev-list --count HEAD)
  if [ -n "$PLIST" ]; then
    /usr/libexec/PlistBuddy -c "Set :CFBundleVersion $BUILD_NUMBER" "$PLIST" 2>/dev/null \
      || /usr/libexec/PlistBuddy -c "Add :CFBundleVersion string $BUILD_NUMBER" "$PLIST"
  fi

  # The C# side finds the native functions by name at runtime, so nothing references them at link time.
  # force_load keeps every object in the library and turning off dead stripping keeps their symbols.
  echo "Archiving in Xcode (build $BUILD_NUMBER, log: $OUT/archive.log)..."
  xcodebuild archive -project "$XCODE_PROJ" -scheme "$SCHEME" -configuration Release \
    -destination "generic/platform=iOS" -archivePath "$ARCHIVE" -allowProvisioningUpdates \
    DEVELOPMENT_TEAM="$TEAM" CODE_SIGN_STYLE=Automatic PRODUCT_BUNDLE_IDENTIFIER="$BUNDLE_ID" \
    CURRENT_PROJECT_VERSION="$BUILD_NUMBER" TARGETED_DEVICE_FAMILY=1 IPHONEOS_DEPLOYMENT_TARGET="$MIN_IOS" \
    DEAD_CODE_STRIPPING=NO OTHER_LDFLAGS="\$(inherited) -Wl,-force_load,$STATIC_LIB" \
    > "$OUT/archive.log" 2>&1 \
    || { grep -E 'error:|Undefined symbol|warning: .*[Pp]rovisioning' "$OUT/archive.log" | head -20
         echo "Archive failed. Full log: $OUT/archive.log"; exit 1; }
  echo "Archived: $ARCHIVE"
fi

[ "$UPLOAD" = 1 ] || { echo "Skipped the upload (--no-upload)."; exit 0; }
[ -d "$ARCHIVE" ] || { echo "No archive at $ARCHIVE. Run without --upload-only first."; exit 1; }

cat > "$OUT/ExportOptions.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>method</key><string>app-store-connect</string>
  <key>destination</key><string>upload</string>
  <key>teamID</key><string>$TEAM</string>
  <key>signingStyle</key><string>automatic</string>
  <key>uploadSymbols</key><true/>
</dict>
</plist>
PLIST

echo "Uploading to App Store Connect (log: $OUT/upload.log)..."
if ! xcodebuild -exportArchive -archivePath "$ARCHIVE" -exportOptionsPlist "$OUT/ExportOptions.plist" \
    -exportPath "$OUT/export" -allowProvisioningUpdates > "$OUT/upload.log" 2>&1; then
  grep -E 'error:|Error' "$OUT/upload.log" | head -20
  echo "Upload failed. Full log: $OUT/upload.log"
  echo "If it says no app record was found: create the app in App Store Connect (bundle ID $BUNDLE_ID),"
  echo "then run: bash ios/build-ios.sh --upload-only"
  exit 1
fi
echo "Uploaded. It shows up in App Store Connect > TestFlight once Apple finishes processing it."
