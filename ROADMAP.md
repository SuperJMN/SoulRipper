# Roadmap

State of SoulRipper and what is left, in order. Measurements come from the
test ROMs: Super Mario Land (SML), Super Mario Land 2 (SML2), Tetris, Kirby's
Dream Land (GB); Super Mario Bros. 1 and 3, Mega Man 2, Zelda (NES).

## Done

- C# tool (`soulripper`, PackageId `SoulRipper.Tool`) replacing the Python
  scripts, FCEUX and `gbsweep`: discovery engine, worker pool, NSF/GBS
  players, online hints (No-Intro + joshw.info), VGM writer, output folders,
  draft profiles; 50 unit tests.
- Silencing the base state: request stoppers (`$FF` first), validated so
  that garbage values that hang or reset the game and pause flags are
  rejected; driver-RAM reset as a fallback; set-based silence check (some
  drivers run twice in a frame). SML now rebases on `DFE8 = FF`.
- Captures grouped by identical start (SMB3: 44 captures instead of 1272, 21 s).
- Values a community rip never requests on its queue are marked suspect.
- SMB1 and SML2 rip with no profile: a driver that writes the same
  registers every frame (SMB1 keeps the APU off on its title screen) counts
  as idle, so Start is pressed; the NES stack is page 1 at the real `S`
  (a fixed `0x0100` hid `$00C0-$00FF`, where SMB1 keeps its effect queues).
- Effects end when they stop being heard, not at their last write: both
  machines measure their audio output, so a Game Boy note fading through its
  envelope is kept (SML2's coin: 1.1 s instead of 0.08 s), and requests that
  write the chip but are never heard are dropped (17 garbage SML2 values).
- NES DMC samples: each sample start keeps the bytes it plays, read from
  cartridge memory as banked at that moment, and the VGM loads them into the
  player's APU RAM (data blocks of type `0xC2`) only when it does not already
  hold them; a loop loads its own. SMB3's Music 01 now plays its two final
  samples (bytes match the PRG ROM; 73 blocks across the rip, Music 26 down
  from 49 to 2).
- SMB2 (USA, Rev A) names match its sounds: NSF playlists number tracks
  from 1 and were read from 0, so every name went to the next track's
  request (a 0.65 s effect was "It's just a Dream..."). The 14 names now
  agree with the playlist lengths ("It's just a Dream..." 70.6 s + 29.8 s
  loop = 100.4 s); SMB1's 13 too ("Running About" is the 88.7 s overworld,
  not the 12.6 s underground). Sounds that outlast the 15 s reference no
  longer loop on the idle driver's writes: SMB2's title music is a 28.5 s
  jingle instead of music with a silent one-frame loop. SMB2's 8 files
  that played nothing are DMC-only effects (queue `$0601`); with the DMC
  samples above, all 33 of its files are audible.
- Deterministic captures: AprNes' savestate leaves out the APU channels
  (timers, volumes, envelopes, sweeps, length counters live in unmanaged
  arrays), so a loaded state kept playing what the previous experiment of
  that process left sounding; `NesMachine` now saves them itself. Both
  machines also drop the audio of a frame cut short by a crash when they
  load a state, and a crashed capture no longer leaves its sample observer
  chained. Before, SMB2 gave an extra 0.02 s effect (`0603_80`) in one of
  three rips and SML a 0.02 s `DFE8_D8` right after the crashing `DFE8_D7`;
  now repeated rips of SMB2, SMB3 and SML, and SMB2 with `--jobs 1`, give
  identical `sounds.json` and byte-identical files. `--jobs 1` works again:
  its items now go through JSON text as they do to workers.
- Emulator libraries published to NuGet (`Zafiro.GameBoy.Debug.Emulator`,
  `Zafiro.Nes.Debug.Emulator`); public repository; Fleet project; first
  release (0.1.0, tagged).

## 1. Quality of the rips

0. **Success rate on a batch of games.** Rip some 50 NES and 50 Game Boy
   ROMs with no profile and record which fail and why (no triggers, wrong
   reach, request mechanism the engine does not cover). It tells whether
   the remaining work is a handful of generic strategies or a long tail.
1. **Loop detection on real music.** On SML (silenced base), most tracks
   (`DFE8` 03-09, 0B, 11-13) run to the 240 s cap without a loop. Check what
   changes in the "static RAM" state hash between periods (frozen bytes,
   counters) and why the write-hash fallback misses them.
2. **Time per game.** SML takes 11-16 min, almost all of it captures that
   hit the cap (garbage music values and unlooped tracks). Ideas: once a
   community rip lists the values of a queue, capture those first and the
   rest with a short cap; stop a capture early when its writes stop matching
   any plausible music (e.g. writes to unused registers, silence).
3. **Re-check every test ROM** with the current engine and record the
   results here: Tetris (found DFE0/DFE8/DFF0/DFF8, rebased on `DFE8 = FF`,
   138 sounds, too many musics), Kirby, SMB1, Zelda, MM2 (only 2 false
   queues: its requests go through a ring buffer, not a byte), SML2.
4. **Suspect rules for garbage music.** Music that never loops or ends
   within the cap is likely garbage; consider marking it suspect.
5. **Effect names.** Community rips name music only. Effects could be named
   from the code that requests them (e.g. the request next to a BCD
   increment of a counter), but that does not generalise; a curated name
   database keyed by ROM CRC and request would.
6. **NSF rips without playlists** (SMB3, Zelda): no track names. Look for
   NSFe sets (`tlbl` chunk) or other playlist sources.
7. **Ring-buffer request queues** (Mega Man 2): requests written to a
   buffer by a game routine. Detect the routine that writes the buffer and
   call it, or poke buffer + index together.

## 2. Nice to have

- `--keep-suspect` per kind; a summary of suspects by reason.
- Game Boy Color double speed and other systems (SMS/Game Gear PSG, whose
  debug core already exists in Zafiro.MasterSystem.Debug).
- WAV rendering of each VGM for quick listening.
