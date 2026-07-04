#!/usr/bin/env python3
"""Reusable NES-APU VGM writer.

Takes a list of per-frame APU register writes (as captured from an emulator)
and produces a valid VGM (v1.61) targeting the NES APU / RP2A03.
"""
import struct

NES_APU_CLOCK = 1789772          # NTSC 2A03 CPU/APU clock (Hz)
SAMPLES_PER_FRAME = 735          # 44100 / 60
FRAME_WAIT = 0x62                # VGM "wait 735 samples"


def write_vgm(path, writes, *, channel_enable=0x1F, tail_frames=2, gd3=None):
    """writes: list of (frame:int, addr:int, value:int), frame-ordered.
    channel_enable: value prepended to $4015 so the VGM's initially-disabled
                    APU matches the running console state.
    gd3: dict with optional keys track/game/system/author/date/notes.
    """
    if not writes:
        raise ValueError("no writes to encode")
    last_frame = max(w[0] for w in writes)

    by_frame = {}
    for fr, addr, val in writes:
        by_frame.setdefault(fr, []).append((addr, val))

    cmds = bytearray()
    total_samples = 0

    def apu(addr, val):
        cmds.extend((0xB4, addr & 0x1F, val & 0xFF))

    def wait():
        nonlocal total_samples
        cmds.append(FRAME_WAIT)
        total_samples += SAMPLES_PER_FRAME

    # The VGM APU boots with all channels disabled ($4015=0); restore the
    # console's enable state before replaying the captured stream.
    apu(0x15, channel_enable)

    for fr in range(0, last_frame + 1):
        for addr, val in by_frame.get(fr, []):
            apu(addr, val)
        wait()

    apu(0x15, 0x00)                 # silence at the end
    for _ in range(tail_frames):
        wait()
    cmds.append(0x66)               # end of sound data

    # ---- GD3 tag ----
    g = gd3 or {}
    def u16z(s):
        return (s or "").encode("utf-16-le") + b"\x00\x00"
    fields = [
        g.get("track", ""), "",
        g.get("game", ""), "",
        g.get("system", "NES / Famicom"), "",
        g.get("author", ""), "",
        g.get("date", ""), "",
        g.get("notes", ""),
    ]
    gd3_body = b"".join(u16z(s) for s in fields)
    gd3_blk = b"Gd3 " + struct.pack("<II", 0x100, len(gd3_body)) + gd3_body

    # ---- assemble ----
    HEADER = 0x100
    data = bytes(cmds)
    gd3_pos = HEADER + len(data)
    eof_pos = gd3_pos + len(gd3_blk)

    hdr = bytearray(HEADER)
    hdr[0:4] = b"Vgm "
    def put(off, val):
        hdr[off:off + 4] = struct.pack("<I", val)
    put(0x04, eof_pos - 4)          # EOF offset
    put(0x08, 0x00000161)           # version 1.61
    put(0x14, gd3_pos - 0x14)       # GD3 offset
    put(0x18, total_samples)        # total samples
    put(0x24, 60)                   # rate
    put(0x34, HEADER - 0x34)        # VGM data offset
    put(0x84, NES_APU_CLOCK)        # NES APU clock

    with open(path, "wb") as f:
        f.write(hdr)
        f.write(data)
        f.write(gd3_blk)

    return {"samples": total_samples, "seconds": total_samples / 44100.0,
            "bytes": eof_pos, "frames": last_frame + 1}
