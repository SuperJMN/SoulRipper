#!/usr/bin/env python3
"""Multi-game NES SFX -> VGM ripper.

Drives FCEUX (headless) with a generated Lua script that, for each sound
effect declared in a game profile, restores a clean savestate, pokes the
sound-queue RAM byte, and records the APU register writes until the effect
finishes.  Each captured effect is written to its own VGM file.

Usage:
    python3 ripper.py --rom smb3.nes --profile profiles/smb3.json --out out/SMB3
"""
import argparse
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from vgm_writer import write_vgm

FCEUX = os.path.join(HERE, "tools", "fceux", "usr", "games", "fceux")
FCEUX_LIBS = os.path.join(HERE, "tools", "fceux", "usr", "lib", "x86_64-linux-gnu")

LUA_TEMPLATE = r"""
-- AUTO-GENERATED sweep script
local OUT = "{capture_path}"
local BOOT = {boot_frames}
local MAXF = {max_frames}
local log = io.open(OUT, "w")

local SFX = {{
{sfx_table}
}}

local capturing = false
local relframe = 0
local function is_audio(a)
  return (a >= 0x4000 and a <= 0x4013) or a == 0x4015 or a == 0x4017
end
local function onwrite(a, sz, v)
  if not capturing then return end
  if not is_audio(a) then return end
  log:write(string.format("W %d %04X %02X\n", relframe, a, v))
end
memory.registerwrite(0x4000, 0x18, onwrite)
if emu.speedmode then emu.speedmode("maximum") end

for i = 1, BOOT do emu.frameadvance() end
local snap = savestate.create()
savestate.save(snap)
emu.frameadvance()   -- ensure the state is fully written before first load

for idx = 1, #SFX do
  local s = SFX[idx]
  savestate.load(snap)
  log:write(string.format("SFX %d %s\n", idx, s.id))
  memory.writebyte(s.queue, s.value)
  capturing = true
  local active = false
  for f = 0, MAXF - 1 do
    relframe = f
    emu.frameadvance()
    local cur = memory.readbyte(s.done)
    if cur ~= 0 then active = true end
    if active and cur == 0 then
      log:write(string.format("END %d\n", f))
      break
    end
  end
  capturing = false
  log:write("SFXEND\n")
end
log:close()
if emu.exit then emu.exit() else os.exit() end
"""


def gen_lua(profile, capture_path):
    rows = []
    for s in profile["sfx"]:
        rows.append(
            "  {id=%r, queue=0x%04X, value=0x%02X, done=0x%04X},"
            % (s["id"], s["queue"], s["value"], s["done_watch"])
        )
    return LUA_TEMPLATE.format(
        capture_path=capture_path,
        boot_frames=profile["boot_frames"],
        max_frames=profile["max_frames"],
        sfx_table="\n".join(rows),
    )


def run_fceux(rom, lua_path, log_path):
    env = dict(os.environ)
    env["LD_LIBRARY_PATH"] = FCEUX_LIBS + ":" + env.get("LD_LIBRARY_PATH", "")
    env["QT_QPA_PLATFORM"] = "offscreen"
    with open(log_path, "wb") as log:
        # FCEUX segfaults harmlessly on teardown; ignore the return code.
        subprocess.run(
            [FCEUX, "--sound", "0", "--loadlua", lua_path, rom],
            env=env, stdout=log, stderr=subprocess.STDOUT, timeout=300,
        )


def parse_capture(path):
    """-> dict id -> list of (frame, addr, value)"""
    effects = {}
    cur = None
    for line in open(path):
        p = line.split()
        if not p:
            continue
        if p[0] == "SFX":
            cur = p[2]
            effects[cur] = []
        elif p[0] == "W" and cur is not None:
            effects[cur].append((int(p[1]), int(p[2], 16), int(p[3], 16)))
        elif p[0] == "SFXEND":
            cur = None
    return effects


# Channel registers that indicate an actual sound (pulse/tri/noise + DMC data)
def has_output(writes):
    for _, addr, _ in writes:
        if 0x4000 <= addr <= 0x400F or 0x4011 <= addr <= 0x4013:
            return True
    return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rom", required=True)
    ap.add_argument("--profile", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--keep-empty", action="store_true",
                    help="also write VGMs for effects that produced no audio")
    args = ap.parse_args()

    profile = json.load(open(args.profile))
    out_dir = os.path.abspath(args.out)
    rom = os.path.abspath(args.rom)
    os.makedirs(out_dir, exist_ok=True)

    capture_path = os.path.join(out_dir, "_capture.txt")
    lua_path = os.path.join(out_dir, "_sweep.lua")
    log_path = os.path.join(out_dir, "_fceux.log")

    open(lua_path, "w").write(gen_lua(profile, capture_path))

    print(f"[*] {profile['name']}: sweeping {len(profile['sfx'])} effects via FCEUX...")
    run_fceux(rom, lua_path, log_path)

    if not os.path.exists(capture_path):
        print("[!] no capture produced - see", log_path)
        sys.exit(1)

    effects = parse_capture(capture_path)
    meta = {k: profile.get(k) for k in ("name", "system", "author", "date")}

    written, empty = 0, []
    for i, s in enumerate(profile["sfx"], 1):
        writes = effects.get(s["id"], [])
        if not writes or not has_output(writes):
            empty.append(s["id"])
            if not args.keep_empty:
                continue
        fname = f"{i:02d}_{s['id']}.vgm"
        gd3 = {
            "track": s["name"],
            "game": meta["name"],
            "system": meta["system"],
            "author": meta["author"],
            "date": meta["date"],
            "notes": profile.get("notes", ""),
        }
        info = write_vgm(
            os.path.join(out_dir, fname), writes,
            channel_enable=profile.get("channel_enable", 0x1F),
            gd3=gd3,
        )
        written += 1
        print(f"    [{i:02d}] {s['name']:<22} {info['seconds']:.2f}s  "
              f"{len(writes)} writes -> {fname}")

    print(f"[*] done: {written} VGM(s) in {out_dir}")
    if empty:
        print(f"[i] {len(empty)} effect(s) produced no audio in this state "
              f"(likely need an in-level savestate): {', '.join(empty)}")


if __name__ == "__main__":
    main()
