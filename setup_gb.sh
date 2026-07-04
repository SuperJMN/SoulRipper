#!/usr/bin/env bash
# Build the Game Boy sweeper (gb/GbSweep). Requires the .NET SDK and the
# GameboyMcp repo checked out as a sibling of this repo (both in ~/Repos),
# or pass its path:  ./setup_gb.sh /path/to/GameboyMcp
set -euo pipefail
cd "$(dirname "$0")"

MCP_DIR="${1:-$(cd .. && pwd)/GameboyMcp}"
if [ ! -d "$MCP_DIR/src/GameBoy.Debug.Emulator" ]; then
  echo "[!] GameboyMcp not found at: $MCP_DIR"
  echo "    Clone it as a sibling repo or pass its path: ./setup_gb.sh /path/to/GameboyMcp"
  exit 1
fi

echo "[*] building gbsweep against $MCP_DIR ..."
dotnet build -c Release gb/GbSweep/GbSweep.csproj -p:GbMcpDir="$MCP_DIR" -v minimal
echo "[*] gbsweep ready: gb/GbSweep/bin/Release/net10.0/gbsweep.dll"
