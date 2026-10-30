"""
Cuts Forecaster's thunder clips (BepInEx/plugins/Forecaster/thunder/*.wav) out of freely licensed field
recordings from Wikimedia Commons. Credits for every source are in THUNDER_CREDITS.md and the README.

Each clip is one strike: from just before its onset to the end of its rumble, faded at both ends, mono,
loudness-matched, and labelled "near" (a sharp crack at the start) or "far" (a rumble). The game adds the
distance itself (delay after the flash, quieter, more muffled the further away).

Usage: python tools/thunder_clips.py <folder with the downloaded recordings> <output folder>
Then: python tools/derain.py <output folder>   (takes out the recordings' own rain)
Needs numpy and soundfile (and ffmpeg on the PATH for Oggs libsndfile can't open).
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
    # 0.2.3: more variety (Joel, 2026-10-30), strikes picked with auto_strikes, 20 dB or more over the rain;
    # four more were dropped for the rain left in them after tools/derain.py.
    ("Very_close_thunder_cracks.flac", 6.25, 10.5),
    ("Very_close_thunder_cracks.flac", 19.75, 13.0),
    ("Storm_thunderbolts.ogg", 5.0, 9.5),
    ("Storm_thunderbolts.ogg", 34.5, 7.75),
    ("Storm_thunderbolts.ogg", 67.25, 6.75),
    ("Storm_thunderbolts.ogg", 72.0, 8.75),
    ("Storm_thunderbolts.ogg", 87.75, 13.5),
    ("Storm_thunderbolts.ogg", 157.5, 6.0),
    ("Booming_Thunder_2023-10-09_2-07-34_am.ogg", 9.75, 16.0),
    ("Thunder_01.ogg", 0.5, 6.0),
    ("Thunder_and_rain_on_a_v.ogg", 6.0, 11.5),
    ("Truenos_y_lluvia_(sonido)_07.wav", 1.25, 16.0),
    ("Truenos_y_lluvia_(sonido)_11.wav", 6.25, 11.0),
    ("Thunderstorm_after_hot_summer_day_17_minutes_01_of_04.ogg", 159.25, 8.5),
    ("Thunderstorm_after_hot_summer_day_17_minutes_02_of_04.ogg", 21.5, 7.0),
    ("Thunderstorm_after_hot_summer_day_17_minutes_02_of_04.ogg", 226.25, 16.0),
    ("Thunderstorm_after_hot_summer_day_17_minutes_04_of_04.ogg", 4.25, 6.75),
]


_cache = {}


def read(path):
    """A recording as mono at RATE. Some Commons Oggs are in a form libsndfile can't open; ffmpeg converts those."""
    if path not in _cache:
        try:
            x, r = sf.read(path, always_2d=True)
        except Exception:
            import subprocess, tempfile
            tmp = os.path.join(tempfile.mkdtemp(), "x.wav")
            subprocess.run(["ffmpeg", "-nostdin", "-loglevel", "error", "-y", "-i", path, "-ac", "1", "-ar", str(RATE), tmp], check=True)
            x, r = sf.read(tmp, always_2d=True)
        _cache[path] = resample(x.mean(axis=1), r)
    return _cache[path]


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
    made = []
    # Numbered by place in STRIKES, so a missing source never renumbers the clips after it.
    for n, (name, start, length) in enumerate(STRIKES, 1):
        path = os.path.join(src, name)
        if not os.path.exists(path) or os.path.getsize(path) == 0:
            print("missing", name)
            continue
        x = read(path)
        for s, l in [(start, length)]:
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
            fn = f"thunder_{k}_{n:02d}.wav"
            # Plain 16-bit WAV, which the plugin reads itself: Unity's Ogg Vorbis loader, in EFT, turned
            # these clips (written by libsndfile) into empty, 0 s AudioClips (0.2.1, 2026-10-29).
            sf.write(os.path.join(out, fn), seg.astype(np.float32), RATE, format="WAV", subtype="PCM_16")
            made.append({"file": fn, "source": name, "start": round(s, 2), "seconds": round(len(seg) / RATE, 1), "kind": k, "bright": round(float(bright), 3)})
            print(fn, made[-1])
    json.dump(made, open(os.path.join(out, "clips.json"), "w"), indent=1)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
