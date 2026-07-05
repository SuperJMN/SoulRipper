# SFX → VGM ripper (NES + Game Boy)

Automated extraction of **sound effects** from **NES** and **Game Boy** ROMs into
per-effect **VGM** files. For each effect declared in a game *profile*, the ripper
drives an emulator to capture the sound-chip register writes the effect produces,
then encodes each capture as its own VGM.

Because the VGM stores the raw register writes, playback is **cycle-exact** on any
compatible player (NSFPlay/SameBoy cores, foobar2000/Winamp + vgmstream, etc.).

| System | Engine | Isolation strategy | Trigger |
|--------|--------|--------------------|---------|
| NES | headless **FCEUX** + Lua | per-effect **savestate** from a silent state | RAM poke of the sound-queue byte |
| Game Boy | **CoreBoy** (`gb/GbSweep`) | **differential capture** (run twice, subtract music) | RAM poke of the sound-request byte |

## Why profiles?
ROMs have no standard "play SFX N" API (unlike music via NSF/GBS subsongs).
Each game's sound engine triggers effects its own way — usually by writing an ID
to a *sound request* RAM byte. A profile captures that game-specific knowledge:
which RAM address to poke, the effect IDs, and how to reach a state where the
effect plays.

## Usage
```bash
# NES
./setup_fceux.sh                      # one-time: fetch a local, no-root FCEUX
python3 ripper.py \
    --rom /path/to/game.nes \
    --profile profiles/smb3.json \
    --out out/SMB3

# Game Boy
./setup_gb.sh                         # one-time: build gbsweep (needs .NET SDK +
                                      # sibling GameboyMcp repo, see below)
python3 ripper.py \
    --rom /path/to/game.gb \
    --profile profiles/sml.json \
    --out out/SML

# Super Mario Land 2 directly from a zipped ROM
python3 ripper.py \
    --rom "/mnt/store/Juegos/ROMs/Gameboy/Super Mario Land 2 - 6 Golden Coins (USA, Europe).zip" \
    --profile profiles/sml2.json \
    --out out/SML2
```
Output: `out/<GAME>/NN_<Name>.vgm`, one per effect that produced audio.
The system (NES vs GB) is auto-detected from the profile's `system` field.
Effects that stayed silent are listed at the end.
`--rom` may point to a plain ROM or to a `.zip` containing exactly one
supported ROM (`.nes`, `.gb`, or `.gbc`).

## Game Boy backend (`gb/GbSweep`)
`gbsweep` is a small C# tool that drives the **CoreBoy**-based debug session from
the [GameboyMcp](https://github.com/) repo (pure managed, no native deps). It is
expected as a **sibling checkout** (`~/Repos/GameboyMcp` next to this repo);
`setup_gb.sh` accepts an explicit path otherwise.

Because Game Boy games play music during gameplay, GB effects are isolated by
**differential capture**: the sweeper reaches a deterministic state, records the
APU writes for a music-only reference run, then re-reaches the same state and
records again with the SFX request poked. Only writes the effect *changes* are
kept — the byte-identical music cancels out, leaving the effect alone on its
channel. See `profiles/sml.json` (Super Mario Land, sound requests via `$DFE0`).
`profiles/sml2.json` covers Super Mario Land 2 by driving the engine's three
SFX request queues: `$A460` (square/pulse), `$A470` (wave), and `$A478` (noise).

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
- `ripper.py` — orchestrator: selects the backend by system, parses captures,
  writes VGMs.
- `vgm_writer.py` — reusable VGM encoder for NES APU **and** Game Boy DMG.
- `gb/GbSweep/` — C# Game Boy sweeper (CoreBoy; `record` and `sweep`/diff modes).
- `profiles/smb3.json` — Super Mario Bros. 3, NES (31 effects).
- `profiles/sml.json` — Super Mario Land, Game Boy (11 square-channel effects).
- `profiles/sml2.json` — Super Mario Land 2, Game Boy (57 engine SFX queue values).
- `setup_fceux.sh` — fetch local FCEUX (no root).
- `setup_gb.sh` — build the Game Boy sweeper.
