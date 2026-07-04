#!/usr/bin/env python3
"""Reusable VGM writer for NES APU (RP2A03) and Game Boy DMG.

Takes a list of per-frame sound-chip register writes (as captured from an
emulator) and produces a valid VGM v1.61.

- NES:  command 0xB4, register = addr - 0x4000, clock 1789772 @ header 0x84.
- GB:   command 0xB3, register = addr - 0xFF10, clock 4194304 @ header 0x80.
"""
import struct

VGM_SAMPLE_RATE = 44100

# --- chip definitions -------------------------------------------------------
NES = {
    "clock": 1789772,           # NTSC 2A03
    "clock_ofs": 0x84,          # VGM header offset for NES APU clock
    "cmd": 0xB4,                # register-write command
    "reg_base": 0x4000,
    "fps": 60.0,                # NTSC frame rate used for wait timing
    "system": "NES / Famicom",
}
GB = {
    "clock": 4194304,           # DMG CPU/APU clock
    "clock_ofs": 0x80,          # VGM header offset for GameBoy DMG clock
    "cmd": 0xB3,
    "reg_base": 0xFF10,
    "fps": 4194304 / 70224,     # ~59.7275 Hz (70224 cycles/frame)
    "system": "Game Boy",
}


def _wait_cmd(cmds, samples):
    # VGM 0x61 nn nn : wait n samples
    cmds.extend((0x61, samples & 0xFF, (samples >> 8) & 0xFF))


def _encode(path, chip, writes, *, init_writes, silence, tail_frames, gd3):
    """writes: list of (frame:int, addr:int, value:int), frame-ordered."""
    if not writes:
        raise ValueError("no writes to encode")
    last_frame = max(w[0] for w in writes)

    by_frame = {}
    for fr, addr, val in writes:
        by_frame.setdefault(fr, []).append((addr, val))

    cmds = bytearray()
    total_samples = 0
    frac = 0.0
    samples_per_frame = VGM_SAMPLE_RATE / chip["fps"]

    def reg(addr, val):
        cmds.extend((chip["cmd"], (addr - chip["reg_base"]) & 0xFF, val & 0xFF))

    def frame_wait():
        nonlocal total_samples, frac
        frac += samples_per_frame
        n = int(round(frac))
        frac -= n
        _wait_cmd(cmds, n)
        total_samples += n

    # Restore the chip's power/enable state (the VGM chip resets silent, but the
    # console had already configured these before the capture window began).
    for addr, val in init_writes:
        reg(addr, val)

    for fr in range(0, last_frame + 1):
        for addr, val in by_frame.get(fr, []):
            reg(addr, val)
        frame_wait()

    for addr, val in silence:            # silence the chip at the end
        reg(addr, val)
    for _ in range(tail_frames):
        frame_wait()
    cmds.append(0x66)                    # end of sound data

    # ---- GD3 tag ----
    g = gd3 or {}
    def u16z(s):
        return (s or "").encode("utf-16-le") + b"\x00\x00"
    fields = [
        g.get("track", ""), "",
        g.get("game", ""), "",
        g.get("system", chip["system"]), "",
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
    put(chip["clock_ofs"], chip["clock"])

    with open(path, "wb") as f:
        f.write(hdr)
        f.write(data)
        f.write(gd3_blk)

    return {"samples": total_samples, "seconds": total_samples / VGM_SAMPLE_RATE,
            "bytes": eof_pos, "frames": last_frame + 1}


# --- public API -------------------------------------------------------------
def write_vgm(path, writes, *, channel_enable=0x1F, tail_frames=2, gd3=None):
    """NES-APU VGM. channel_enable -> prepended $4015 value."""
    return _encode(path, NES, writes,
                   init_writes=[(0x4015, channel_enable)],
                   silence=[(0x4015, 0x00)],
                   tail_frames=tail_frames, gd3=gd3)


def write_vgm_gb(path, writes, *, tail_frames=3, gd3=None):
    """Game Boy DMG VGM. Prepends a sane APU power/volume/pan state so the
    captured channel writes take effect (NR52 power, NR50 volume, NR51 pan)."""
    return _encode(path, GB, writes,
                   init_writes=[(0xFF26, 0x80),   # NR52: APU power on
                                (0xFF24, 0x77),   # NR50: master volume L/R
                                (0xFF25, 0xFF)],  # NR51: all channels to L+R
                   silence=[(0xFF26, 0x00)],
                   tail_frames=tail_frames, gd3=gd3)
