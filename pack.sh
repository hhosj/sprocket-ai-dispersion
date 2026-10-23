#!/usr/bin/env bash
# Builds the plugin and produces the downloadable artifacts in release/.
#
# release/ is tracked in git on purpose: the README links straight to these files, so there is
# a working download link without depending on a GitHub Release existing.
#
# The "mod zip" deliberately contains ONLY the plugin DLL: that is the form the community
# Mod Manager expects for its "Add mod" button (it reads the DLL's .NET metadata and drops it
# into BepInEx/plugins).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION="1.0.0"
NAME="Sprocket.AiDispersion"
OUT="$HERE/release"
DLL="$OUT/$NAME.dll"

# 1. Build (uses whatever .NET SDK build.sh is configured for).
"$HERE/build.sh" >/dev/null
[ -f "$DLL" ] || { echo "build produced no DLL" >&2; exit 1; }

# 2. Zip containing only the DLL.
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cp -f "$DLL" "$WORK/$NAME.dll"
rm -f "$OUT/$NAME-$VERSION.zip"
( cd "$WORK" && zip -q -X "$OUT/$NAME-$VERSION.zip" "$NAME.dll" )

# 3. Checksums.
( cd "$OUT" && sha256sum "$NAME-$VERSION.zip" | awk '{print $1}' > "$NAME-$VERSION.zip.sha256" )
( cd "$OUT" && sha256sum "$NAME.dll"           | awk '{print $1}' > "$NAME.dll.sha256" )
cat > "$OUT/SHA256SUMS.txt" <<EOF
$(cat "$OUT/$NAME-$VERSION.zip.sha256")  $NAME-$VERSION.zip
$(cat "$OUT/$NAME.dll.sha256")  $NAME.dll
EOF

echo "artifacts in release/:"
ls -la "$OUT"
echo
cat "$OUT/SHA256SUMS.txt"
