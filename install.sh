#!/usr/bin/env bash
# Installs the patched BepInEx loader and this plugin into the Sprocket folder.
#
# Run ./get-bepinex.sh first - Sprocket needs BepInEx be.788 PLUS the community
# compatibility patch (stock BepInEx black-screens on this game build).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GAME="${SPROCKET_DIR:-$HOME/.local/share/Steam/steamapps/common/Sprocket}"

if [[ ! -f "$GAME/Sprocket.exe" ]]; then
  echo "Sprocket.exe not found in: $GAME" >&2
  echo "Set SPROCKET_DIR=/path/to/Sprocket and try again." >&2
  exit 1
fi

if [[ ! -f "$HERE/third_party/BepInEx/core/BepInEx.Core.dll" || ! -f "$HERE/third_party/.sprocket_patch_applied" ]]; then
  echo "Patched loader not prepared yet, running get-bepinex.sh ..."
  "$HERE/get-bepinex.sh"
fi

DLL="$HERE/dist/Sprocket.AiDispersion.dll"
if [[ ! -f "$DLL" ]]; then
  DLL="$HERE/src/SprocketAiDispersion/bin/Release/net6.0/Sprocket.AiDispersion.dll"
fi
if [[ ! -f "$DLL" ]]; then
  echo "Plugin not built yet, running build.sh ..."
  "$HERE/build.sh"
  DLL="$HERE/src/SprocketAiDispersion/bin/Release/net6.0/Sprocket.AiDispersion.dll"
fi

echo "Installing into: $GAME"
echo
echo "NOTE: if you already have a loader installed, back up these first:"
echo "  BepInEx/  dotnet/  winhttp.dll  doorstop_config.ini  .doorstop_version"
echo

# 1. Loader: BepInEx be.788 + the patched Il2CppInterop bridge DLLs.
cp -rn "$HERE/third_party/." "$GAME/" 2>/dev/null || cp -r "$HERE/third_party/." "$GAME/"

# 2. The plugin.
mkdir -p "$GAME/BepInEx/plugins"
cp -f "$DLL" "$GAME/BepInEx/plugins/"

echo "Done. Loader files now present:"
ls -1 "$GAME" | grep -i -E "winhttp|doorstop|BepInEx|dotnet" || true
echo "Patched bridge DLLs:"
ls -1 "$GAME/BepInEx/core" | grep -i -E "Il2CppInterop|TerraFX" || true
echo "Plugins:"
ls -1 "$GAME/BepInEx/plugins"
echo
cat <<'NOTE'
Required Steam launch option (Proton / Linux):
    WINEDLLOVERRIDES="winhttp=n,b" %command%

First launch takes 1-3 minutes (BepInEx generates interop assemblies).
Before that it needs a 3 MB "Unity base libraries" zip in
    <game>/BepInEx/unity-libs/<unityVersion>.zip
If it is missing or the download stalls, run ./get-unity-libs.sh.
Look for "runtime traced" in <game>/BepInEx/LogOutput.log, then in-game:
    Mod config: <game>/BepInEx/config/sprocket.ai.dispersion.cfg
NOTE

# Warn about the Unity base libraries step - it is the usual place to get stuck.
if ! ls "$GAME"/BepInEx/unity-libs/*.zip >/dev/null 2>&1; then
  echo
  echo "WARNING: BepInEx/unity-libs/*.zip is not present."
  echo "         BepInEx will try to download it on first launch and may hang."
  echo "         Run:  ./get-unity-libs.sh"
fi
