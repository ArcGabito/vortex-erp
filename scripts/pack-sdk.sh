#!/usr/bin/env bash
# Empaqueta el SDK del hub (VortexApi.Client) en el feed local del ERP: packages/vortex-sdk.
# Cada ejecución genera una versión -dev nueva; la app toma siempre la más alta (VortexApi.Client 0.*-*).
#
#   scripts/pack-sdk.sh                              # hub clonado en ~/vortex-api
#   VORTEX_API_DIR=/ruta/a/vortex-api scripts/pack-sdk.sh
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
hub="${VORTEX_API_DIR:-$HOME/vortex-api}"

if [[ ! -x "$hub/scripts/pack-sdk.sh" ]]; then
  echo "No se encontró el hub en $hub. Clónalo con: git clone https://github.com/ArcGabito/vortex-api \"$hub\"" >&2
  exit 1
fi

VORTEX_FEED="$root/packages/vortex-sdk" "$hub/scripts/pack-sdk.sh"
