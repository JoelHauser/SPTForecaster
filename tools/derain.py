"""
Takes the rain out of Forecaster's thunder clips. Most of the source recordings were made in the storm, so
the clips carry their own rain hiss under the thunder, and in game that played on top of the game's own rain
(Joel, 2026-10-29: "dual rain noises").

Rain is a steady broadband hiss; thunder is a crack and a rumble that come and go. Per clip, the hiss's
spectrum is taken from the clip's quietest moments (a low percentile of each frequency over time), and
spectral subtraction takes about twice that out of every frame, never more than MAX_CUT_DB, smoothed over
time and frequency so it doesn't warble. Below LOW_HZ nothing is touched: that's the rumble, and rain adds
little there. Then the clip is loudness-matched again as tools/thunder_clips.py does.

Usage: python tools/derain.py <clip folder> [<output folder>]   (in place if no output folder)
Needs numpy only (16-bit mono or stereo WAV).
"""
import os
import sys
import wave

import numpy as np

N, HOP = 2048, 512
LOW_HZ, FULL_HZ = 350.0, 900.0  # untouched below LOW_HZ, full strength above FULL_HZ
OVER = 3.0                      # how many times the hiss estimate to take out
MAX_CUT_DB = 40.0
NOISE_PERCENTILE = 15

# Raindrops hitting the microphone are clicks, not hiss, so the subtraction leaves them. Thunder has little
# above a couple of kHz once its crack is over (the air takes the highs), so after the loudest high-frequency
# moment in the first TAIL_SEARCH_S, everything above TAIL_HZ fades by TAIL_CUT_DB over TAIL_FADE_S.
TAIL_HZ, TAIL_FULL_HZ = 1500.0, 3000.0
TAIL_SEARCH_S, TAIL_HOLD_S, TAIL_FADE_S, TAIL_CUT_DB = 3.0, 1.0, 1.0, 35.0


def read(path):
    with wave.open(path) as w:
        r, ch = w.getframerate(), w.getnchannels()
        x = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float64) / 32768.0
    return x.reshape(-1, ch).mean(axis=1), r


def write(path, x, r):
    y = np.clip(np.round(x * 32767.0), -32768, 32767).astype(np.int16)
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(r)
        w.writeframes(y.tobytes())


def smooth(a, k, axis):
    if k <= 1:
        return a
    kernel = np.ones(k) / k
    return np.apply_along_axis(lambda v: np.convolve(v, kernel, mode="same"), axis, a)


def derain(x, r):
    pad = np.concatenate([np.zeros(N), x, np.zeros(N)])
    win = np.hanning(N)
    frames = np.lib.stride_tricks.sliding_window_view(pad, N)[::HOP] * win
    spec = np.fft.rfft(frames, axis=1)
    mag = np.abs(spec)
    freqs = np.fft.rfftfreq(N, 1.0 / r)

    noise = np.percentile(mag, NOISE_PERCENTILE, axis=0)
    gain = 1.0 - OVER * (noise[None, :] / (mag + 1e-12))
    gain = np.maximum(gain, 10 ** (-MAX_CUT_DB / 20.0))
    gain = smooth(smooth(gain, 5, 0), 3, 1)          # over ~60 ms and 3 bins
    weight = np.clip((freqs - LOW_HZ) / (FULL_HZ - LOW_HZ), 0.0, 1.0)
    gain = 1.0 - weight[None, :] * (1.0 - gain)

    # The tail: highs fade out after the crack.
    times = (np.arange(len(mag)) * HOP - N) / r
    high = (mag[:, freqs > TAIL_FULL_HZ] ** 2).sum(axis=1)
    search = times < TAIL_SEARCH_S
    crack = times[np.argmax(np.where(search, high, -1))]
    fade = np.clip((times - crack - TAIL_HOLD_S) / TAIL_FADE_S, 0.0, 1.0)
    tail = 10 ** (-TAIL_CUT_DB * fade / 20.0)
    above = np.clip((freqs - TAIL_HZ) / (TAIL_FULL_HZ - TAIL_HZ), 0.0, 1.0)
    gain = gain * (1.0 - above[None, :] * (1.0 - tail[:, None]))

    out_frames = np.fft.irfft(spec * gain, n=N, axis=1) * win
    out = np.zeros(len(pad))
    norm = np.zeros(len(pad))
    for i, f in enumerate(out_frames):
        out[i * HOP:i * HOP + N] += f
        norm[i * HOP:i * HOP + N] += win ** 2
    out = out / np.maximum(norm, 1e-6)
    return out[N:N + len(x)]


def loudness_match(x, r):
    rms = np.sqrt(np.mean(x[: 3 * r] ** 2)) + 1e-9
    x = x * (0.12 / rms)
    peak = np.abs(x).max()
    return x * (0.89 / peak) if peak > 0.89 else x


def main(src, out):
    os.makedirs(out, exist_ok=True)
    for name in sorted(os.listdir(src)):
        if not name.lower().endswith(".wav"):
            continue
        x, r = read(os.path.join(src, name))
        y = loudness_match(derain(x, r), r)
        write(os.path.join(out, name), y, r)
        print(name)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else sys.argv[1])
