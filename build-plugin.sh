#!/usr/bin/env bash
# ============================================================
#  SP2 Pack — build the in-game loader plugin (BepInEx 5 / Mono / x64)
#
#  References: the game's own Managed/*.dll plus plugin/refs
#              (BepInEx.dll and 0Harmony.dll).
#  Output:     plugin/SP2WorldModelLoader.dll
#
#  Usage:  ./build-plugin.sh
#  Override the game folder with SP2_GAME_DIR if needed.
# ============================================================
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
GAME="${SP2_GAME_DIR:-/Users/Yangbiao/Desktop/SimplePlanes 2}"

# Show paths without the home folder prefix (Desktop/... instead of /Users/name/Desktop/...).
pretty() { case "$1" in "$HOME"/*) printf '%s' "${1#"$HOME"/}" ;; *) printf '%s' "$1" ;; esac; }
MANAGED="$GAME/SimplePlanes 2_Data/Managed"
SRC="$HERE/Plugin~/src/Plugin.cs"
OUT="$HERE/Plugin~/SP2WorldModelLoader.dll"
RSP="$HERE/Plugin~/refs.rsp"
VENDOR="$HERE/Plugin~/vendor-BepInEx_win_x64_5.4.23.5.zip"
REFS="$HERE/Plugin~/refs"

[ -f "$SRC" ]     || { echo "missing source: $(pretty "$SRC")" >&2; exit 1; }
[ -d "$MANAGED" ] || { echo "game Managed folder not found: $(pretty "$MANAGED")" >&2; echo "Set SP2_GAME_DIR to the SimplePlanes 2 folder." >&2; exit 1; }

# BepInEx reference assemblies are not committed; extract them from the bundled zip once.
if [ ! -f "$REFS/BepInEx.dll" ] || [ ! -f "$REFS/0Harmony.dll" ]; then
  [ -f "$VENDOR" ] || { echo "missing BepInEx zip: $(pretty "$VENDOR")" >&2; exit 1; }
  echo "extracting plugin references from the bundled BepInEx zip..."
  TMP="$HERE/Plugin~/.refs-tmp"
  rm -rf "$TMP"; mkdir -p "$TMP" "$REFS"
  unzip -oq "$VENDOR" "BepInEx/core/BepInEx.dll" "BepInEx/core/0Harmony.dll" -d "$TMP"
  cp "$TMP/BepInEx/core/BepInEx.dll" "$REFS/"
  cp "$TMP/BepInEx/core/0Harmony.dll" "$REFS/"
  rm -rf "$TMP"
fi

CSC="$(ls /usr/local/share/dotnet/sdk/*/Roslyn/bincore/csc.dll 2>/dev/null | tail -1 || true)"
[ -n "$CSC" ] || { echo "csc not found (a dotnet SDK is required)" >&2; exit 1; }

# Build the reference list
: > "$RSP"
for f in "$MANAGED"/*.dll; do echo "-r:\"$f\"" >> "$RSP"; done
for f in "$REFS"/*.dll; do echo "-r:\"$f\"" >> "$RSP"; done

echo "csc   : $CSC"
echo "game  : $(pretty "$MANAGED")"
echo "refs  : $(wc -l < "$RSP" | tr -d ' ') assemblies"

dotnet "$CSC" -nologo -noconfig -target:library -langversion:latest -nullable:disable \
  -out:"$OUT" @"$RSP" "$SRC"

echo "OK -> $(pretty "$OUT")"
