# NES SFX → VGM ripper

Automated extraction of **sound effects** from NES ROMs into per-effect **VGM**
files. Drives a headless **FCEUX** with a generated Lua script that, for each
effect declared in a game *profile*, restores a clean savestate, pokes the
sound-engine's RAM trigger, and records the APU register writes until the
effect finishes. Each capture is encoded to its own VGM.

Because the VGM stores the raw APU register writes, playback is **cycle-exact**
on any NES-APU-capable player (NSFPlay core, Winamp/foobar + vgmstream, etc.).

## Why profiles?
NES ROMs have no standard "play SFX N" API (unlike music via NSF subsongs).
Each game's sound engine triggers effects its own way — usually by writing an
ID to a *sound queue* RAM byte. A profile captures that game-specific knowledge:
which RAM address to poke, the effect IDs, and how to detect "effect finished".

## Usage
```bash
./setup_fceux.sh                      # one-time: fetch a local, no-root FCEUX
python3 ripper.py \
    --rom /path/to/game.nes \
    --profile profiles/smb3.json \
    --out out/SMB3
```
Output: `out/SMB3/NN_<Queue>_<Name>.vgm`, one per effect that produced audio.
Effects that stay silent in the boot state are listed at the end (they usually
need an in-level savestate — see "Limitations").

## Profile format (`profiles/*.json`)
```jsonc
{
  "name": "Super Mario Bros. 3",
  "system": "NES / Famicom",
  "author": "Koji Kondo",
  "date": "1988",
  "boot_frames": 181,        // frames to run to reach a clean/silent state
  "max_frames": 240,         // capture cap per effect
  "channel_enable": 31,      // value prepended to $4015 ($1F = all channels)
  "sfx": [
    { "name": "Player - Jump", "id": "Player_Jump",
      "queue": 1265,          // 0x04F1  RAM byte to poke
      "value": 1,             // 0x01    effect ID
      "done_watch": 1249 }    // 0x04E1  nonzero while playing, 0 = finished
  ]
}
```
`profiles/smb3.json` was derived from the Southbird SMB3 disassembly
(sound queues `$04F1–$04F7`, "currently playing" mirrors `$04E1–$04E7`).

## How it works
1. `ripper.py` renders a Lua sweep script from the profile.
2. FCEUX runs headless (`QT_QPA_PLATFORM=offscreen`, `--sound 0`), boots to a
   silent frame, and snapshots a savestate.
3. For each effect: load snapshot → poke `queue = value` → record every write to
   `$4000–$4017` until `done_watch` returns to 0 (or `max_frames`).
4. `vgm_writer.py` encodes each per-frame write list to VGM v1.61 (NES APU,
   clock 1789772 Hz), prepending a `$4015` enable so the initially-disabled
   VGM APU matches the running console.

## Limitations
- **Context-dependent effects.** Effects that only play in a specific game state
  (some level/map sounds) may be silent when triggered from the boot state.
  Fix: add a profile option to load an in-level savestate before sweeping.
- **Sustained effects** (e.g. SMB3 "power ring") must be re-triggered every
  frame; a single poke yields no lasting output. Currently reported as "no
  audio".
- **Music vs SFX.** Song/jingle queues are intentionally excluded.

## Adding a new game
1. Find the sound engine's trigger RAM byte(s) and effect IDs (disassembly,
   datacrystal, or the discovery method below).
2. Optionally find a "currently playing" RAM byte for clean end-detection;
   otherwise rely on `max_frames`.
3. Write `profiles/<game>.json` and run.

### Discovery (no disassembly)
Log RAM writes alongside APU-register bursts during play and correlate: RAM
bytes whose writes reliably precede APU channel activity are trigger
candidates. Then sweep their values.

## Files
- `ripper.py` — orchestrator (FCEUX driver + capture parser).
- `vgm_writer.py` — reusable NES-APU VGM encoder.
- `profiles/smb3.json` — Super Mario Bros. 3 (31 effects).
- `setup_fceux.sh` — fetch local FCEUX (no root).
