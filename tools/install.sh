#!/usr/bin/env bash
# Installs BepInEx 5 + the Dorf Helper plugin into a local Dorfromantik (Steam, macOS) install.
#
#   tools/install.sh                      # default Steam location
#   tools/install.sh --game-dir "/path/to/Dorfromantik"
#   tools/install.sh --skip-build         # set up BepInEx only
#   tools/install.sh --prebuilt           # download the plugin from the latest GitHub release instead of building it
#                                         # (automatic when the .NET SDK is not installed)
#
# Safe to run again: files are overwritten, game files themselves are never modified.
set -euo pipefail

BEPINEX_VERSION="5.4.23.5"
UNITY_VERSION="2021.3.45"   # the game ships Unity 2021.3.45f2

GAME_DIR="${DORFROMANTIK_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Dorfromantik}"
SKIP_BUILD=0
PREBUILT=0
RELEASE_DLL_URL="https://github.com/leGoupil/dorfromantik-mod/releases/latest/download/DorfHelper.dll"
while [ $# -gt 0 ]; do
    case "$1" in
        --game-dir) GAME_DIR="$2"; shift 2 ;;
        --skip-build) SKIP_BUILD=1; shift ;;
        --prebuilt) PREBUILT=1; shift ;;
        -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
CACHE_DIR="$REPO_DIR/vendor"
# Standalone package (e.g. the release zip): DorfHelper.dll next to this script is installed as is.
LOCAL_DLL=""
[ -f "$SCRIPT_DIR/DorfHelper.dll" ] && [ ! -f "$REPO_DIR/src/DorfHelper/DorfHelper.csproj" ] && LOCAL_DLL="$SCRIPT_DIR/DorfHelper.dll"
[ -n "$LOCAL_DLL" ] && CACHE_DIR="$SCRIPT_DIR/vendor"
APP="$GAME_DIR/Dorfromantik.app"

step() { printf '\n==> %s\n' "$*"; }
fail() { printf 'Error: %s\n' "$*" >&2; exit 1; }

[ -d "$APP" ] || fail "Dorfromantik.app not found in: $GAME_DIR (use --game-dir)"
command -v curl >/dev/null || fail "curl is required"
command -v unzip >/dev/null || fail "unzip is required"
if [ -n "$LOCAL_DLL" ]; then
    PREBUILT=1
elif [ "$SKIP_BUILD" = 0 ] && [ "$PREBUILT" = 0 ] && ! command -v dotnet >/dev/null; then
    echo "The .NET SDK is not installed: the prebuilt plugin will be downloaded from the latest release."
    PREBUILT=1
fi

mkdir -p "$CACHE_DIR"
download() { # url file
    if [ ! -s "$CACHE_DIR/$2" ]; then
        echo "    downloading $2"
        curl -fsSL -o "$CACHE_DIR/$2" "$1"
    else
        echo "    using cached $2"
    fi
}

step "Downloading BepInEx $BEPINEX_VERSION and full Unity $UNITY_VERSION libraries"
download "https://github.com/BepInEx/BepInEx/releases/download/v$BEPINEX_VERSION/BepInEx_macos_universal_$BEPINEX_VERSION.zip" \
         "BepInEx_macos_universal_$BEPINEX_VERSION.zip"
# The game ships stripped mscorlib / UnityEngine assemblies: BepInEx and the plugin need the full ones.
download "https://unity.bepinex.dev/corlibs/$UNITY_VERSION.zip" "corlibs-$UNITY_VERSION.zip"
download "https://unity.bepinex.dev/libraries/$UNITY_VERSION.zip" "unity-libs-$UNITY_VERSION.zip"

step "Installing BepInEx into $GAME_DIR"
unzip -oq "$CACHE_DIR/BepInEx_macos_universal_$BEPINEX_VERSION.zip" -d "$GAME_DIR"
mkdir -p "$GAME_DIR/BepInEx/unstripped_corlib" "$GAME_DIR/BepInEx/plugins" "$GAME_DIR/BepInEx/config"
unzip -oq "$CACHE_DIR/corlibs-$UNITY_VERSION.zip" -d "$GAME_DIR/BepInEx/unstripped_corlib"
unzip -oq "$CACHE_DIR/unity-libs-$UNITY_VERSION.zip" -d "$GAME_DIR/BepInEx/unstripped_corlib"

step "Configuring the launcher (run_bepinex.sh)"
RUN="$GAME_DIR/run_bepinex.sh"
chmod +x "$RUN"
# Game executable, full libraries searched first, and Rosetta (BepInEx 5's MonoMod/Harmony has no arm64 support).
sed -i '' 's|^executable_name=.*|executable_name="Dorfromantik.app"|' "$RUN"
sed -i '' 's|^dll_search_path_override=.*|dll_search_path_override="BepInEx/unstripped_corlib"|' "$RUN"
sed -i '' 's|export ARCHPREFERENCE="arm64,x86_64"|export ARCHPREFERENCE="x86_64,arm64"|' "$RUN"

step "Configuring BepInEx (HideManagerGameObject)"
CFG="$GAME_DIR/BepInEx/config/BepInEx.cfg"
# The game destroys BepInEx's manager object when loading a game scene; hiding it keeps plugins alive.
if [ -f "$CFG" ] && grep -q '^HideManagerGameObject' "$CFG"; then
    sed -i '' 's/^HideManagerGameObject = .*/HideManagerGameObject = true/' "$CFG"
else
    printf '\n[Chainloader]\n\nHideManagerGameObject = true\n' >> "$CFG"
fi

if [ "$SKIP_BUILD" = 0 ] && [ -n "$LOCAL_DLL" ]; then
    step "Installing the bundled Dorf Helper plugin"
    cp "$LOCAL_DLL" "$GAME_DIR/BepInEx/plugins/DorfHelper.dll"
elif [ "$SKIP_BUILD" = 0 ] && [ "$PREBUILT" = 1 ]; then
    step "Downloading the prebuilt Dorf Helper plugin (latest release)"
    curl -fsSL -o "$GAME_DIR/BepInEx/plugins/DorfHelper.dll" "$RELEASE_DLL_URL" \
        || fail "could not download $RELEASE_DLL_URL"
elif [ "$SKIP_BUILD" = 0 ]; then
    step "Building and installing the Dorf Helper plugin"
    dotnet build -c Release "$REPO_DIR/src/DorfHelper/DorfHelper.csproj" -p:GameDir="$GAME_DIR" -nologo -v quiet
    [ -f "$GAME_DIR/BepInEx/plugins/DorfHelper.dll" ] || fail "build succeeded but DorfHelper.dll was not copied to BepInEx/plugins"
fi

step "Done"
cat <<EOF

Last step, in Steam: Dorfromantik > Properties > General > Launch options, paste:

    "$RUN" %command%

Clear that field to play the unmodded game again.
Logs: $GAME_DIR/BepInEx/LogOutput.log
EOF
