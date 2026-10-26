# SoulRipper

Rips the **music, jingles and sound effects** of **NES** and **Game Boy** ROMs
into one **VGM** file each, with loop points for music. No per-game setup: it
finds how the game asks its sound driver for sounds by itself, and names the
tracks from community rips when it can find them online.

The VGMs store the raw sound-chip register writes, so playback is
**cycle-exact** on any compatible player (foobar2000/Winamp + vgmstream,
Benben, NSFPlay/SameBoy cores, etc.).

## Install

```bash
dotnet tool install -g SoulRipper
```

Or run it without installing, with .NET 10's `dnx`:

```bash
dnx SoulRipper game.gb
```

## Usage

```bash
soul-ripper "Super Mario Land (World).gb"
```

The ROM can be a `.nes`, `.gb` or `.gbc` file, or a zip/7z holding one.
Output goes to a folder named after the game:

- `music/`, `jingles/`, `sfx/`: `NN - Name.vgz` (gzipped VGM; `--vgm` writes
  them uncompressed), one per distinct sound.
  Doubtful captures (requests past the end of the game's sound table) are
  skipped, or written to a `suspect/` subfolder with `--keep-suspect`.
- `sounds.json`: every sound with its request byte and value, length, loop
  and aliases (other values that play the same sound).
- `profile.json`: a draft profile, to curate and pass back with `--profile`.

| Option | |
|--------|---|
| `--out DIR` | output folder (default: the game's name) |
| `--profile P` | curated profile: how to reach a playable state, and sound names |
| `--boot N` | frames to boot before probing (default 600) |
| `--max-seconds S` | longest capture per sound (default 240) |
| `--keep-suspect` | also write doubtful sounds |
| `--offline` | do not look the game up online |
| `--jobs N` | worker processes (default: cores - 1) |

A run takes from under a minute to a few minutes, depending on the game.

## How it works

Games ask their sound driver for a sound by writing its ID into a RAM byte
the driver polls. SoulRipper finds those bytes without knowing the game:

1. **Analysis.** Run the game and note which code writes the sound chip (the
   driver) and which RAM bytes that code reads: the candidates.
2. **Sweep.** From a saved state, poke each candidate with a few values and
   then all 256. A request byte makes the chip react and is *consumed*: the
   driver clears it, unlike channel state, which keeps the poked value.
3. **Silence.** If music is playing, find the driver's "stop" request and
   start over from silence, so effects are recorded on their own. A stopper
   only counts if the driver still answers afterwards: garbage values can
   silence it by hanging it, and pause flags silence it for good.
4. **Capture.** Record each request until its sound ends or loops. Values
   whose first frames sound exactly alike are one sound (an alias table, or
   the same garbage), so only one of them is captured. While capturing, the
   game's own clock is frozen (RAM written by non-driver code is restored
   every frame), so demo modes and timers do not interfere. Loops are
   detected on the driver's state and confirmed on the register writes.
5. **Classify.** Long multi-channel sounds that loop are music, ones that end
   are jingles, the rest effects.

The work runs in parallel on a pool of worker processes, each with its own
emulator: [Zafiro.GameBoy.Debug](https://github.com/SuperJMN/Zafiro.GameBoy.Debug)
(CoreBoy) and [Zafiro.Nes.Debug](https://github.com/SuperJMN/Zafiro.Nes.Debug) (AprNes).

### Online hints

Unless `--offline`, the ROM is identified by CRC32 in the No-Intro DATs
([libretro-database](https://github.com/libretro/libretro-database)) and its
NSF/GBS set is fetched from [joshw.info](https://joshw.info):

- its playlists give **track names** ("World Music 1", "Game Over", ...);
- playing the rip shows which RAM bytes its INIT stores the request in; those
  are probed even if analysis missed them (e.g. SMB3's music queue `$04F5`);
- each rip track is paired with the capture it requests or sounds like, and
  the capture takes its name.

Downloads are cached in `~/.cache/soul-ripper`.

## Profiles

A profile is optional knowledge about a game. Every field is optional:

```jsonc
{
  "name": "Super Mario Land",
  "author": "Hirokazu Tanaka",
  "date": "1989",
  "reach": {                       // how to get from power-on to a playable state
    "boot": 240,
    "presses": [ { "buttons": ["Start"], "hold": 8 } ],
    "settle": 40
  },
  "music": [ { "name": "Overworld", "queue": "0xDFE8", "value": 7 } ],
  "sfx":   [ { "name": "Jump",      "queue": "0xDFE0", "value": 1 } ]
}
```

The entries name the sounds they request, and their queues are always
probed. `profiles/` holds curated ones for Super Mario Land, Super Mario
Land 2 and Super Mario Bros. 3; `profile.json` in every output is a draft to
start from.

## Advanced commands

```bash
soul-ripper discover game.gb --out report.json [--queues A460,A468] [--extra 04F5] [--no-rebase]
soul-ripper bench game.gb [--frames N]          # emulation speed, savestate replay check
soul-ripper access game.gb --addr A460,A470     # code that reads/writes those bytes
```

`discover` writes the raw result (driver, every probed byte, every capture);
`--queues` probes only those bytes, `--extra` adds bytes to the search.

## Limitations

- **Context-dependent sounds.** Sounds that only play in a given game state
  may stay silent from the state the ROM boots into; a profile `reach` can
  get the game there first.
- **Sustained effects** that must be re-requested every frame yield only
  their first frame.
- **Event-triggered sounds** that have no request byte (the driver plays them
  straight from game code) are not found.

## Build

```bash
dotnet build
dotnet test
dotnet run --project src/SoulRipper -- game.gb
```

Releases are published to NuGet by
[DotnetDeployer](https://github.com/SuperJMN/DotnetDeployer) (`deployer.yaml`)
on every merge to `master`.

## License

MIT. The packaged tool includes the AprNes NES core, which carries the
WTFPL and GPL-3.0-or-later licenses (see Zafiro.Nes.Debug's third-party
notices).
