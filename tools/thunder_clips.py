"""
Cuts SPTForecaster's thunder clips (BepInEx/plugins/SPTForecaster/thunder/*.ogg) out of freely licensed field
recordings from Wikimedia Commons. Credits for every source are in THUNDER_CREDITS.md and the README.

Each clip is one strike: from just before its onset to the end of its rumble, faded at both ends, mono,
loudness-matched, and labelled "near" (a sharp crack at the start) or "far" (a rumble). The game adds the
distance itself (delay after the flash, quieter, more muffled the further away).

Usage: python tools/thunder_clips.py <folder with the downloaded recordings> <output folder>
Needs numpy and soundfile.
"""
import json
import os
import sys

import numpy as np
import soundfile as sf

RATE = 44100

# (file, start s, length s) per strike, picked from the recordings' loud events.
STRIKES = [
    ("Lightning_Recorded_In_Groningen_2018-05-13_01.wav", 0.5, 9.0),
    ("Lightning_Recorded_In_Groningen_2018-05-13_02.wav", 0.5, 18.0),
    ("Lightning_Recorded_In_Groningen_2018-05-13_05.wav", 0.5, 8.0),
    ("Nosferatu_thunderclap_-_Richard_Humphries.wav", 2.5, 16.0),
    ("Rhumphries_-_rbh-thunder-storm.ogg", 3.5, 10.0),
    ("Rhumphries_-_rbh-thunder-storm.ogg", 16.5, 10.0),
    ("Rhumphries_-_rbh-thunder-storm.ogg", 43.5, 10.0),
    ("Rhumphries_-_rbh-thunder-storm.ogg", 84.5, 8.0),
    ("Rhumphries_-_rbh-thunder-storm.ogg", 98.5, 10.0),
    ("Thunder_Claps.ogg", 3.5, 19.0),
    ("Thunder_Claps.ogg", 22.5, 6.0),
    ("Thunder_crack_2B.ogg", 3.5, 7.0),
    ("Very_close_thunder_cracks.flac", None, None),   # strikes found automatically if the file is there
]


def resample(x, r):
    if r == RATE:
        return x
    t = np.arange(int(len(x) * RATE / r)) / RATE
    return np.interp(t, np.arange(len(x)) / r, x)


def envelope_db(x, win):
    n = len(x) // win
    rms = np.sqrt(np.mean(x[: n * win].reshape(n, win) ** 2, axis=1)) + 1e-9
    return 20 * np.log10(rms)


def auto_strikes(x):
    """Loud events in a recording: (start s, length s)."""
    win = int(RATE * 0.25)
    db = envelope_db(x, win)
    floor = np.percentile(db, 20)
    out, i = [], 0
    while i < len(db):
        if db[i] > floor + 15:
            j = i
            while j < len(db) and db[j] > floor + 6:
                j += 1
            out.append((max(0, i * 0.25 - 0.5), min(16, (j - i) * 0.25 + 3)))
            i = j + 4
        else:
            i += 1
    return out


def cut(x, start, length):
    a = int(start * RATE)
    seg = x[a: a + int(length * RATE)].copy()
    # Onset: first 10 ms window 12 dB over the clip's quiet floor; keep 80 ms before it.
    win = int(RATE * 0.01)
    db = envelope_db(seg, win)
    floor = np.percentile(db, 10)
    on = next((i for i, v in enumerate(db) if v > floor + 12), 0)
    seg = seg[max(0, on * win - int(0.08 * RATE)):]
    # End: where the rumble has fallen back near the floor for good (or the cut's end).
    db = envelope_db(seg, int(RATE * 0.1))
    loud = np.where(db > floor + 4)[0]
    end = min(len(seg), (loud[-1] + 6) * int(RATE * 0.1)) if len(loud) else len(seg)
    seg = seg[:end]
    fi, fo = int(0.015 * RATE), min(int(1.5 * RATE), len(seg) // 3)
    seg[:fi] *= np.linspace(0, 1, fi)
    seg[-fo:] *= np.linspace(1, 0, fo) ** 1.5
    return seg


def kind(seg):
    """near: a sharp, bright crack at the start; far: mostly low rumble."""
    head = seg[: int(0.5 * RATE)]
    sp = np.abs(np.fft.rfft(head)) ** 2
    fr = np.fft.rfftfreq(len(head), 1 / RATE)
    bright = sp[fr > 1500].sum() / sp.sum()
    peak_at = np.argmax(np.abs(seg)) / RATE
    return ("near" if bright > 0.012 and peak_at < 1.5 else "far"), bright


def main(src, out):
    os.makedirs(out, exist_ok=True)
    made, n = [], 0
    for name, start, length in STRIKES:
        path = os.path.join(src, name)
        if not os.path.exists(path) or os.path.getsize(path) == 0:
            print("missing", name)
            continue
        x, r = sf.read(path, always_2d=True)
        x = resample(x.mean(axis=1), r)
        for s, l in ([(start, length)] if start is not None else auto_strikes(x)):
            seg = cut(x, s, l)
            if len(seg) < RATE * 2:
                continue
            # Loudness match on the strike's first 3 s (RMS), then keep peaks under -1 dBFS.
            rms = np.sqrt(np.mean(seg[: 3 * RATE] ** 2)) + 1e-9
            seg *= 0.12 / rms
            peak = np.abs(seg).max()
            if peak > 0.89:
                seg *= 0.89 / peak
            k, bright = kind(seg)
            n += 1
            fn = f"thunder_{k}_{n:02d}.ogg"
            # In blocks: libsndfile's Vorbis encoder can crash on one large write.
            with sf.SoundFile(os.path.join(out, fn), "w", RATE, 1, format="OGG", subtype="VORBIS") as f:
                data = seg.astype(np.float32)
                for i in range(0, len(data), 4096):
                    f.write(data[i:i + 4096])
            made.append({"file": fn, "source": name, "start": round(s, 2), "seconds": round(len(seg) / RATE, 1), "kind": k, "bright": round(float(bright), 3)})
            print(fn, made[-1])
    json.dump(made, open(os.path.join(out, "clips.json"), "w"), indent=1)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
