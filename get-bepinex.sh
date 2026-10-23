#!/usr/bin/env bash
# Prepares everything Sprocket needs to run BepInEx mods, verified, into third_party/.
#
# Why this is not "just BepInEx":
#   Sprocket is Unity 6000.3.21f1 / IL2CPP metadata v39. Stock BepInEx 6 be.788 boots, but its
#   Il2CppInterop bridge makes wrong assumptions about this game's native code (it pattern-matches
#   the wrong Unity function) and the game black-screens or crashes. The community "Sprocket Mod
#   Loader" pack repairs three bridge targets for this exact runtime; it replaces these five DLLs:
#       Il2CppInterop.Runtime.dll  Il2CppInterop.Common.dll  Il2CppInterop.HarmonySupport.dll
#       Il2CppInterop.Generator.dll  TerraFX.Interop.Windows.dll
#
# So we assemble: BepInEx be.788 (the pack's required base)
#                 + the patched bridge (Sprocket Mod Loader 1.2.3)
#
# Where the pieces come from (both GitHub, both checksum-verified):
#   * BepInEx be.788 + Doorstop + .NET 6 runtime: the SprocketSDK "full" release, which is a
#     repackaged be.788 (its Il2CppInterop is byte-identical to nuget Il2CppInterop 1.5.3, the
#     dependency of be.788, and its Doorstop files are timestamped at the be.788 commit).
#     https://github.com/Neemogudmen/SprocketSDK
#     (Official be.788 zip is https://builds.bepinex.dev/projects/bepinex_be/788/... if you
#      prefer it, but that host is unreachable from some networks.)
#   * The patched bridge: https://github.com/Hans21223/Sprocket-Mod-Loader
#
# Everything resumes, so re-run if your connection drops.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="$HERE/third_party"
WORK="$HERE/third_party/.download"

SDK_URL="${SDK_URL:-https://github.com/Neemogudmen/SprocketSDK/releases/download/v0.2.0/SprocketSDK-0.2.0-full.zip}"
SDK_SHA="${SDK_SHA:-5af1d66a53b8d654dae7e8c9332b52c14cfb86b91730fb7dc21455d2d37cb247}"

PATCH_URL="${PATCH_URL:-https://github.com/Hans21223/Sprocket-Mod-Loader/releases/download/v1.4.0/Sprocket-Mod-Loader-1.2.3.zip}"
PATCH_SHA="${PATCH_SHA:-e54f09a863913b96dc2eb15402ee1a8a91ba46d60548c3be04c6e672ec43fe15}"

mkdir -p "$DEST" "$WORK"
cd "$WORK"

if [[ -f "$DEST/BepInEx/core/BepInEx.Core.dll" && -f "$DEST/.sprocket_patch_applied" ]]; then
  echo "Already prepared in $DEST - nothing to do."
  exit 0
fi

fetch() { # url sha file
  local url="$1" sha="$2" file="$3"
  echo "Downloading $(basename "$file") ..."
  for _ in $(seq 1 10); do
    if curl -fL -C - --retry 5 --retry-delay 3 --connect-timeout 30 -o "$file" "$url"; then break; fi
    echo "  interrupted, resuming ..." >&2; sleep 3
  done
  local got; got="$(sha256sum "$file" | cut -d' ' -f1)"
  if [[ "${sha,,}" != "$got" ]]; then
    echo "ERROR: SHA-256 mismatch for $file" >&2
    echo "  expected $sha" >&2
    echo "  got      $got" >&2
    echo "Delete $file and try again (a resumed/partial download leaves a bad file)." >&2
    exit 1
  fi
  echo "  ok ($got)"
}

unpack() { # zip destdir
  rm -rf "$2"; mkdir -p "$2"
  if command -v 7z >/dev/null 2>&1; then 7z x -y -o"$2" "$1" >/dev/null
  else unzip -oq "$1" -d "$2"; fi
}

fetch "$SDK_URL" "$SDK_SHA" "$WORK/base.zip"
fetch "$PATCH_URL" "$PATCH_SHA" "$WORK/patch.zip"

echo "Extracting base loader ..."
unpack "$WORK/base.zip" "$WORK/base"

BASE="$WORK/base"
if [[ ! -f "$BASE/BepInEx/core/BepInEx.Core.dll" ]]; then
  echo "ERROR: unexpected layout in the base zip." >&2; exit 1
fi

if [[ -f "$DEST/.sprocket_patch_applied" ]]; then rm -f "$DEST/.sprocket_patch_applied"; fi
rm -rf "$DEST/BepInEx" "$DEST/dotnet" "$DEST/winhttp.dll" "$DEST/doorstop_config.ini" "$DEST/.doorstop_version"

# Loader (skip the SDK's own patcher/plugin and its Mods folder - we only want BepInEx).
cp -r "$BASE/dotnet" "$BASE/BepInEx" "$DEST/"
cp -f "$BASE/winhttp.dll" "$BASE/doorstop_config.ini" "$BASE/.doorstop_version" "$DEST/"
rm -rf "$DEST/BepInEx/patchers" "$DEST/BepInEx/plugins" "$DEST/BepInEx/README-SprocketSDK.txt" \
       "$DEST/Mods" "$DEST/BepInEx/LICENSE-BepInEx.txt"
mkdir -p "$DEST/BepInEx/plugins" "$DEST/BepInEx/config"

echo "Extracting compatibility patch ..."
unpack "$WORK/patch.zip" "$WORK/patch"
PATCH_CORE="$(find "$WORK/patch" -type d -path '*/Patch/BepInEx/core' | head -1)"
if [[ -z "$PATCH_CORE" ]]; then echo "ERROR: Patch/BepInEx/core not found in patch zip." >&2; exit 1; fi

echo "Overlaying patched bridge DLLs:"
cp -f "$PATCH_CORE"/*.dll "$DEST/BepInEx/core/"
ls -1 "$PATCH_CORE"

# Cross-check the patch's own checksum list.
SUMS="$(find "$WORK/patch" -name SHA256SUMS.txt | head -1)"
if [[ -n "$SUMS" ]]; then
  ( cd "$(dirname "$SUMS")" && grep -E "Patch/BepInEx/core/" SHA256SUMS.txt | sha256sum -c --quiet ) \
    && echo "Patch checksums: OK"
fi

touch "$DEST/.sprocket_patch_applied"
rm -rf "$WORK"

echo
echo "Ready in: $DEST"
echo "Next:  ./build.sh && ./install.sh"
