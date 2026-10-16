#!/usr/bin/env bash
# ============================================================
#  SP2 Pack — package the mod
#  Bundles BepInEx + the loader plugin + the exported airport
#  assets into one "extract and play" zip.
#
#  Usage:
#     ./package.sh [mod name]
#  Example:
#     ./package.sh "Wright Airport"
#
#  Preconditions:
#     1) Export the scene from Unity (Tools > SP2 Pack > Export).
#        This produces Output~/obj/.
#     2) The plugin DLL exists in Plugin~; this script builds it if not.
#
#  Output:
#     Output~/dist/SP2-<mod name>-BepInEx-win-x64.zip
#     Extract into the "SimplePlanes 2" folder
#     (the one that contains SimplePlanes 2.exe).
# ============================================================
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
NAME="${1:-Wright Airport}"
SLUG="$(printf '%s' "$NAME" | tr ' ' '-')"

# Show paths without the home folder prefix (Desktop/... instead of /Users/name/Desktop/...).
pretty() { case "$1" in "$HOME"/*) printf '%s' "${1#"$HOME"/}" ;; *) printf '%s' "$1" ;; esac; }

BEPZIP="$HERE/Plugin~/vendor-BepInEx_win_x64_5.4.23.5.zip"
PLUGIN="$HERE/Plugin~/SP2WorldModelLoader.dll"
ASSETS="$HERE/Output~/obj"
STAGE="$HERE/Output~/stage"
ZIP="$HERE/Output~/dist/SP2-${SLUG}-BepInEx-win-x64.zip"

# --- checks -------------------------------------------------------------
[ -f "$BEPZIP" ] || { echo "missing BepInEx zip: $BEPZIP" >&2; exit 1; }

if [ ! -f "$PLUGIN" ]; then
  echo "plugin DLL missing, building it first..."
  "$HERE/build-plugin.sh"
fi

if [ ! -d "$ASSETS" ] || [ -z "$(ls -A "$ASSETS" 2>/dev/null)" ]; then
  echo "no exported assets at: $(pretty "$ASSETS")" >&2
  echo "Run Tools > SP2 Pack > Export in Unity first." >&2
  exit 1
fi

# --- assemble -----------------------------------------------------------
rm -rf "$STAGE"
mkdir -p "$STAGE"

unzip -oq "$BEPZIP" -d "$STAGE"

PLUGDIR="$STAGE/BepInEx/plugins"
OBJDIR="$PLUGDIR/UniversalWorldModelLoader/Obj"
mkdir -p "$OBJDIR"

cp "$PLUGIN" "$PLUGDIR/"

# Airport assets (obj / mtl / tex)
cp -R "$ASSETS/." "$OBJDIR/"

# Install notes
cat > "$STAGE/INSTALL.txt" <<EOF
SP2 Airport Mod — $NAME
=====================================

INSTALL
-------
Extract everything in this zip into your "SimplePlanes 2" folder
(the one that contains "SimplePlanes 2.exe"). Merge / replace when asked.
BepInEx 5.4.23.5 (x64) is already bundled — do NOT install it separately.

USE
---
1. Start the game and load into the world; stand still for a moment.
2. Press F9 to load the airport, F10 to unload, F7 to open the panel.
3. The airport is attached to terrain SPN_1_2 by default. If the position,
   heading or scale is off, adjust Offset / AxisMode / Scale in the F7 panel
   and note the values once it lines up.

LOG
---
<SimplePlanes 2>\BepInEx\LogOutput.log
EOF

mkdir -p "$(dirname "$ZIP")"
rm -f "$ZIP"
( cd "$STAGE" && zip -qr "$ZIP" . )

echo
echo "OK -> $(pretty "$ZIP")"
echo
echo "package contents:"
( cd "$STAGE" && find . -maxdepth 4 -type f | sort | sed 's#^\./##' | head -40 )
