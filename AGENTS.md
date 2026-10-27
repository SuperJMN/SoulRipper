# AGENTS.md

Guidance for coding agents working in this repository (`CLAUDE.md` imports this file).

SoulRipper is a .NET 10 global tool (`soulripper`, NuGet package `SoulRipper.Tool`) that rips the music, jingles and sound effects of NES and Game Boy ROMs to VGM, with no per-game setup. `README.md` explains the user-facing behaviour and the discovery algorithm; `ROADMAP.md` records what is done, the measured state on the test ROMs and what is left — update it when a change moves one of its items.

## Commands

```bash
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~SuspectsTests"        # one class
dotnet test --filter "FullyQualifiedName~SuspectsTests.<Method>" # one test
dotnet run --project src/SoulRipper -- game.gb [--offline] [--jobs 1]
dotnet run --project src/SoulRipper -- discover game.gb --out report.json [--queues DFE0,DFE8]
dotnet run --project src/SoulRipper -- bench game.gb    # emulation speed + savestate replay check
dotnet run --project src/SoulRipper -- access game.gb --addr DFE0   # code that reads/writes bytes
```

- Tests (xUnit) are pure unit tests over hint parsing, pairing, suspects, profiles and output; they need no ROMs. `tests/SoulRipper.Tests/Sounds.cs` builds `Sound` fixtures.
- Checking the engine itself means running the tool on a real ROM. ROMs and outputs are git-ignored (copyrighted data); never commit them. `--offline` avoids network lookups; `--jobs 1` runs everything in-process (easier to debug than the worker pool).
- Do not rebuild while a rip is running: the worker processes start from the built executable, so a rebuild mid-run can break or skew it.
- Every merge to `master` is a release: DotnetDeployer.Fleet (`deployer.yaml`, GitVersion) packs and pushes to NuGet. The release gate is `dotnet test SoulRipper.slnx -c Release --nologo -m:1`; run exactly that before merging.
- The Fleet project pins the package inventory to `SoulRipper.Tool`. Changing the `PackageId`, or making another project packable (the test project must stay non-packable), fails the release until the project's expected package IDs are updated in Fleet, before merging.

## Architecture

Single project `src/SoulRipper`, everything `internal` (tests get `InternalsVisibleTo`). `Program.cs` dispatches the subcommands `rip` (default), `discover`, `bench`, `access` and the hidden `worker`; `Options.cs` is a hand-rolled parser (boolean flags must be listed in `Options.IsFlag`).

**Machines** (`Machines/`): `IMachine` reduces a console to what discovery needs — run frames, save/load whole-machine state, peek/poke, bus read/write observers with the PC, and an `Audible` flag measured on the audio output. `NesMachine` wraps AprNes (`Zafiro.Nes.Debug.Emulator`), whose core is static: only one NES machine can live in a process, which is why parallel work runs in worker processes rather than threads. `GameBoyMachine` wraps CoreBoy (`Zafiro.GameBoy.Debug.Emulator`); CoreBoy has no savestates, so `ObjectGraphSnapshot` snapshots its object graph by reflection. `SoundChip` describes each chip's register map (owned addresses, channel per register, note-trigger registers, FPS). Emulator internals live in the sibling repos Zafiro.Nes.Debug / Zafiro.GameBoy.Debug, consumed as NuGet packages.

**Engine** (`Engine/`):
- `Reach` is how to get from power-on to the base state (boot frames, button presses, settle). `Program.ReachFor` presses Start when the driver is idle and keeps that idle state for silencing.
- `Prober` runs experiments on one machine from a base state: `Analyze` (find driver PCs that write the chip and the RAM they read → candidate bytes), `Sweep` (poke values, a request byte is *consumed* by the driver), `IsSilentAfter`, `Capture` (record until the sound ends or loops; loops are detected on driver state and confirmed on register writes). Tunables are in `DiscoverySettings`.
- `Discovery` orchestrates analysis → sweep → rebase on silence → freeze game-owned RAM → capture, and classifies sounds as music/jingle/sfx.
- `IWorkPool` parallelises sweep/capture: `LocalPool` (in-process) or `ProcessPool`, which spawns `soulripper worker` processes speaking one JSON line per request/response over stdin/stdout (`Wire` encodes/decodes, `ProberOps` executes). Each worker replays the same `Reach` and applies the same `ProbeContext`; the context carries a base-state checksum and the worker fails if it differs, so **everything that builds the base state must be deterministic**. Workers redirect `Console.Out` to stderr because the cores print to stdout. Any new prober operation or context field must go through `ProberOps`/`Wire` as well.

**Hints** (`Hints/`, `Rips/`): `GameHints.Lookup` identifies the ROM by CRC32 in the No-Intro DATs, downloads its NSF/GBS set from joshw.info (cached in `~/.cache/soulripper`), parses playlists for track names, and plays the rip (`NsfPlayer` on the minimal `Cpu6502`, `GbsPlayer` by wrapping the GBS in an MBC5 ROM on CoreBoy) to learn which RAM byte its INIT stores the request in. Those bytes are added to the candidates, and `TrackPairing`/`SequenceMatcher` pair rip tracks with captures to name them.

**Output** (`Output/`): `RipWriter` writes `music/`, `jingles/`, `sfx/` (`.vgz` by default, `.vgm` with `--vgm`), `sounds.json` and a draft `profile.json`; `Suspects` decides which captures are doubtful (past a gap in the ID table, multi-bit values on flag queues, values the community rip never requests) and are skipped unless `--keep-suspect`. `Report` writes the raw `discover` JSON.

**Profiles** (`Profile.cs`, `profiles/`): optional curated JSON with `name`/`author`/`date`, a `reach`, and `music`/`sfx` entries (`queue` + `value` + `name`) whose queues are always probed. Unknown keys are ignored, so the older fields still in `profiles/*.json` (`isolate`, `capture_frames`, `id`…) have no effect.

## Conventions

- Comments explain *why* in short prose (often naming the game that motivated a rule, e.g. SMB1, SML2); keep that style and that density.
- Commits and docs are in English. Commit messages are plain sentences in the imperative ("End effects when they stop being heard"), no conventional-commit prefixes.
