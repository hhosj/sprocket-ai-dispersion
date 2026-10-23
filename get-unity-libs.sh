#!/usr/bin/env bash
# Fetches the Unity *managed base libraries* zip that Il2CppInterop needs to generate the
# interop assemblies on the first (and first-after-update) launch.
#
#   BepInEx looks for it at  <game>/BepInEx/unity-libs/<unityVersion>.zip
#   and only downloads it if that file does NOT already exist.
#
# unity.bepinex.dev is fast on some networks and throttled to a few KB/s on others, so this
# script resumes in a loop and can simply be re-run until it completes.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
UNITY_VERSION="${UNITY_VERSION:-6000.3.21}"
URL="${UNITY_LIBS_URL:-https://unity.bepinex.dev/libraries/${UNITY_VERSION}.zip}"
GAME="${SPROCKET_DIR:-$HOME/.local/share/Steam/steamapps/common/Sprocket}"
CACHE="$HERE/third_party/BepInEx/unity-libs"
FILE="$CACHE/${UNITY_VERSION}.zip"

mkdir -p "$CACHE"

# Known good checksums. If your Unity version differs, the hash check is skipped and only the
# zip integrity test is used.
declare -A SHA=(
  ["6000.3.21"]="d836329482e33ec5f63a842aa1967cef467a75952b81b7d3d08aa0e7542ee67f"
)
EXPECTED="${SHA[$UNITY_VERSION]:-}"

verify() {
  [[ -f "$FILE" ]] || return 1
  python3 -c "import sys,zipfile; zipfile.ZipFile(sys.argv[1]).testzip()" "$FILE" >/dev/null 2>&1
}

if ! verify; then
  echo "Downloading Unity $UNITY_VERSION base libraries ..."
  for i in $(seq 1 500); do
    if verify; then break; fi
    curl -fL -C - --retry 3 --retry-delay 2 --connect-timeout 20 --max-time 120 -s -o "$FILE" "$URL" || true
    printf '\r  attempt %-3s  %s bytes' "$i" "$(stat -c%s "$FILE" 2>/dev/null || echo 0)"
    sleep 1
  done
  echo
fi

if ! verify; then
  echo "ERROR: could not obtain a complete $UNITY_VERSION.zip" >&2
  echo "Download it yourself and place it at: $FILE" >&2
  exit 1
fi

if [[ -n "$EXPECTED" ]]; then
  got="$(sha256sum "$FILE" | cut -d' ' -f1)"
  if [[ "$got" != "$EXPECTED" ]]; then
    echo "ERROR: SHA-256 mismatch" >&2
    echo "  expected $EXPECTED" >&2
    echo "  got      $got" >&2
    exit 1
  fi
  echo "SHA-256 OK: $got"
fi

if [[ -d "$GAME" ]]; then
  mkdir -p "$GAME/BepInEx/unity-libs"
  cp -f "$FILE" "$GAME/BepInEx/unity-libs/"
  echo "Installed: $GAME/BepInEx/unity-libs/${UNITY_VERSION}.zip"
else
  echo "Cached at: $FILE (game folder not found, copy it manually)"
fi
