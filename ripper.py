#!/usr/bin/env python3
"""Multi-system SFX -> VGM ripper (NES APU and Game Boy DMG).

For each sound effect declared in a game profile, the ripper drives an emulator
to capture the sound-chip register writes the effect produces, then encodes each
capture as its own VGM file.

- NES: headless FCEUX + Lua; per-effect savestate isolation, RAM-poke trigger.
- GB:  CoreBoy-based `gbsweep` (gb/GbSweep); differential capture cancels music.

Usage:
    python3 ripper.py --rom smb3.nes --profile profiles/smb3.json --out out/SMB3
    python3 ripper.py --rom sml.gb   --profile profiles/sml.json  --out out/SML
"""
import argparse
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from vgm_writer import write_vgm, write_vgm_gb

FCEUX = os.path.join(HERE, "tools", "fceux", "usr", "games", "fceux")
FCEUX_LIBS = os.path.join(HERE, "tools", "fceux", "usr", "lib", "x86_64-linux-gnu")
GBSWEEP_DLL = os.path.join(HERE, "gb", "GbSweep", "bin", "Release", "net10.0", "gbsweep.dll")

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


def run_fceux(rom, lua_path, log_path, expected_sfx, timeout=180):
    """Launch headless FCEUX and wait until the Lua has written all effects.

    FCEUX can fail to exit cleanly under the offscreen platform, so rather than
    depend on process exit we poll the capture file for completion (one SFXEND
    per effect) and then terminate the process. This also avoids leaking stuck
    emulator processes across runs.
    """
    import time
    capture_path = None
    # derive capture path from the generated lua (OUT = "...")
    for line in open(lua_path):
        m = re.match(r'\s*local OUT = "(.+)"', line)
        if m:
            capture_path = m.group(1)
            break
    env = dict(os.environ)
    env["LD_LIBRARY_PATH"] = FCEUX_LIBS + ":" + env.get("LD_LIBRARY_PATH", "")
    env["QT_QPA_PLATFORM"] = "offscreen"
    log = open(log_path, "wb")
    proc = subprocess.Popen(
        [FCEUX, "--sound", "0", "--loadlua", lua_path, rom],
        env=env, stdout=log, stderr=subprocess.STDOUT,
    )
    try:
        deadline = time.time() + timeout
        while time.time() < deadline:
            if proc.poll() is not None:
                break                      # FCEUX exited on its own
            if capture_path and os.path.exists(capture_path):
                done = open(capture_path).read().count("SFXEND")
                if done >= expected_sfx:
                    break
            time.sleep(0.3)
    finally:
        if proc.poll() is None:
            proc.kill()
            proc.wait(timeout=10)
        log.close()


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


def has_output_gb(writes):
    for _, addr, _ in writes:
        if 0xFF10 <= addr <= 0xFF25 or 0xFF30 <= addr <= 0xFF3F:
            return True
    return False


def run_gbsweep(rom, profile_path, capture_path, log_path):
    if not os.path.exists(GBSWEEP_DLL):
        print("[!] gbsweep not built. Run:  ./setup_gb.sh   (needs .NET + sibling "
              "GameboyMcp repo)", file=sys.stderr)
        sys.exit(1)
    with open(log_path, "wb") as log:
        subprocess.run(
            ["dotnet", GBSWEEP_DLL, "sweep",
             "--rom", rom, "--profile", profile_path, "--out", capture_path],
            stdout=log, stderr=subprocess.STDOUT, timeout=1200, check=True,
        )


def system_of(profile):
    return "gb" if str(profile.get("system", "")).lower().startswith("game boy") else "nes"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rom", required=True)
    ap.add_argument("--profile", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--system", choices=["auto", "nes", "gb"], default="auto")
    ap.add_argument("--keep-empty", action="store_true",
                    help="also write VGMs for effects that produced no audio")
    args = ap.parse_args()

    profile = json.load(open(args.profile))
    system = system_of(profile) if args.system == "auto" else args.system
    out_dir = os.path.abspath(args.out)
    rom = os.path.abspath(args.rom)
    os.makedirs(out_dir, exist_ok=True)

    capture_path = os.path.join(out_dir, "_capture.txt")
    log_path = os.path.join(out_dir, "_engine.log")

    print(f"[*] {profile['name']} [{system}]: sweeping "
          f"{len(profile['sfx'])} effects...")
    if system == "gb":
        run_gbsweep(rom, os.path.abspath(args.profile), capture_path, log_path)
        vgm_fn = lambda path, writes, gd3: write_vgm_gb(path, writes, gd3=gd3)
        audio_ok = has_output_gb
    else:
        lua_path = os.path.join(out_dir, "_sweep.lua")
        open(lua_path, "w").write(gen_lua(profile, capture_path))
        run_fceux(rom, lua_path, log_path, expected_sfx=len(profile["sfx"]))
        ce = profile.get("channel_enable", 0x1F)
        vgm_fn = lambda path, writes, gd3: write_vgm(path, writes, channel_enable=ce, gd3=gd3)
        audio_ok = has_output

    if not os.path.exists(capture_path):
        print("[!] no capture produced - see", log_path)
        sys.exit(1)

    effects = parse_capture(capture_path)
    meta = {k: profile.get(k) for k in ("name", "system", "author", "date")}

    written, empty = 0, []
    for i, s in enumerate(profile["sfx"], 1):
        writes = effects.get(s["id"], [])
        if not writes or not audio_ok(writes):
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
        info = vgm_fn(os.path.join(out_dir, fname), writes, gd3)
        written += 1
        print(f"    [{i:02d}] {s['name']:<22} {info['seconds']:.2f}s  "
              f"{len(writes)} writes -> {fname}")

    print(f"[*] done: {written} VGM(s) in {out_dir}")
    if empty:
        print(f"[i] {len(empty)} effect(s) produced no audio in this state: "
              f"{', '.join(empty)}")


if __name__ == "__main__":
    main()
