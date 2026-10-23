#!/usr/bin/env bash
# Builds the plugin and produces the release artifacts in dist/.
#
# The "mod zip" deliberately contains ONLY the plugin DLL: that is the form the community
# Mod Manager expects for its "Add mod" button (it reads the DLL's .NET metadata and drops it
# into BepInEx/plugins). Docs live next to the zip, not inside it.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION="1.0.0"
NAME="Sprocket.AiDispersion"
DLL="$HERE/dist/$NAME.dll"

# 1. Build (uses whatever .NET SDK is configured for build.sh).
"$HERE/build.sh" >/dev/null

[ -f "$DLL" ] || { echo "build produced no DLL" >&2; exit 1; }

# 2. Sanity check: the assembly must not reference the game (only BepInEx/Harmony/Il2CppInterop).
if strings -a "$DLL" | grep -q "GameAssembly"; then
  echo "WARNING: DLL references GameAssembly" >&2
fi

# 3. Zip containing only the DLL.
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cp -f "$DLL" "$WORK/$NAME.dll"
rm -f "$HERE/dist/$NAME-$VERSION.zip"
( cd "$WORK" && zip -q -X "$HERE/dist/$NAME-$VERSION.zip" "$NAME.dll" )

# 4. Checksums.
( cd "$HERE/dist" && sha256sum "$NAME-$VERSION.zip" | awk '{print $1}' > "$NAME-$VERSION.zip.sha256" )
( cd "$HERE/dist" && sha256sum "$NAME.dll"           | awk '{print $1}' > "$NAME.dll.sha256" )

cat > "$HERE/dist/校验值.txt" <<EOF
$(cat "$HERE/dist/$NAME-$VERSION.zip.sha256")  $NAME-$VERSION.zip
$(cat "$HERE/dist/$NAME.dll.sha256")  $NAME.dll
EOF

echo "release artifacts:"
ls -la "$HERE/dist"
echo
cat "$HERE/dist/校验值.txt"
