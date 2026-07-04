#!/usr/bin/env bash
# Re-extract a local, no-root FCEUX (with Lua) into tools/fceux.
# FCEUX is only needed to *run* the ripper; the VGM writer is pure Python.
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p tools && cd tools
echo "[*] downloading FCEUX + runtime libs (no root needed)..."
apt-get download fceux liblua5.1-0 libminizip1t64
rm -rf fceux && mkdir fceux
for d in fceux_*.deb liblua5.1-0*.deb libminizip1t64*.deb; do dpkg-deb -x "$d" fceux; done
BIN=fceux/usr/games/fceux
if LD_LIBRARY_PATH=fceux/usr/lib/x86_64-linux-gnu ldd "$BIN" | grep -q "not found"; then
  echo "[!] unresolved libraries:"; LD_LIBRARY_PATH=fceux/usr/lib/x86_64-linux-gnu ldd "$BIN" | grep "not found"
  exit 1
fi
echo "[*] FCEUX ready: $BIN"
