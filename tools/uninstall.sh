#!/usr/bin/env bash
# Removes BepInEx and the Dorf Helper plugin from a local Dorfromantik (Steam, macOS) install.
#
#   tools/uninstall.sh                    # default Steam location, asks for confirmation
#   tools/uninstall.sh --game-dir "/path/to/Dorfromantik"
#   tools/uninstall.sh --yes              # no confirmation
#   tools/uninstall.sh --keep-data        # keep BepInEx/DorfHelper (recorded tiles/quests) and the config
#
# Only removes what tools/install.sh added; the game's own files are never touched.
set -euo pipefail

GAME_DIR="${DORFROMANTIK_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Dorfromantik}"
ASSUME_YES=0
KEEP_DATA=0
while [ $# -gt 0 ]; do
    case "$1" in
        --game-dir) GAME_DIR="$2"; shift 2 ;;
        -y|--yes) ASSUME_YES=1; shift ;;
        --keep-data) KEEP_DATA=1; shift ;;
        -h|--help) sed -n '2,9p' "$0"; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

[ -d "$GAME_DIR/Dorfromantik.app" ] || { echo "Error: Dorfromantik.app not found in: $GAME_DIR (use --game-dir)" >&2; exit 1; }

TARGETS=()
for f in libdoorstop.dylib .doorstop_version run_bepinex.sh; do
    [ -e "$GAME_DIR/$f" ] && TARGETS+=("$GAME_DIR/$f")
done
# BepInEx's own changelog, extracted next to the game by the BepInEx archive
if [ -f "$GAME_DIR/changelog.txt" ] && head -1 "$GAME_DIR/changelog.txt" | grep -q "commits since v5\."; then
    TARGETS+=("$GAME_DIR/changelog.txt")
fi
if [ -d "$GAME_DIR/BepInEx" ]; then
    if [ "$KEEP_DATA" = 1 ]; then
        for d in core plugins unstripped_corlib patchers cache LogOutput.log; do
            [ -e "$GAME_DIR/BepInEx/$d" ] && TARGETS+=("$GAME_DIR/BepInEx/$d")
        done
    else
        TARGETS+=("$GAME_DIR/BepInEx")
    fi
fi

if [ ${#TARGETS[@]} -eq 0 ]; then
    echo "Nothing to remove: BepInEx is not installed in $GAME_DIR"
    exit 0
fi

echo "This will remove:"
printf '  %s\n' "${TARGETS[@]}"
if [ "$ASSUME_YES" = 0 ]; then
    read -r -p "Continue? [y/N] " answer
    case "$answer" in y|Y|yes|YES) ;; *) echo "Cancelled."; exit 0 ;; esac
fi

rm -rf "${TARGETS[@]}"
cat <<EOF
Removed.

Also clear the Steam launch option (Dorfromantik > Properties > General > Launch options),
otherwise Steam will fail to start the game through the deleted run_bepinex.sh.
EOF
