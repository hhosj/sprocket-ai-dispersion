#!/usr/bin/env bash
# Builds the plugin.
#
# Requires a .NET SDK (6.0 or newer - a newer SDK fetches the net6.0 reference pack
# automatically). BepInEx itself is pulled from NuGet (see NuGet.config).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJ="$HERE/src/SprocketAiDispersion/SprocketAiDispersion.csproj"

# Prefer an explicit DOTNET_ROOT, then a local ~/.dotnet, then PATH.
if [[ -n "${DOTNET_ROOT:-}" && -x "${DOTNET_ROOT}/dotnet" ]]; then
  DOTNET="${DOTNET_ROOT}/dotnet"
elif [[ -x "$HOME/.dotnet/dotnet" ]]; then
  DOTNET="$HOME/.dotnet/dotnet"
else
  DOTNET="dotnet"
fi

echo "Using: $("$DOTNET" --version)"
"$DOTNET" build "$PROJ" -c Release "$@"

DLL="$HERE/src/SprocketAiDispersion/bin/Release/net6.0/Sprocket.AiDispersion.dll"
mkdir -p "$HERE/dist"
cp -f "$DLL" "$HERE/dist/"

echo
echo "Built: $HERE/dist/Sprocket.AiDispersion.dll"
echo "Install with: ./install.sh"
