"""
Draws Forecaster's own weather glyphs (src/Forecaster/Glyphs/*.png) for weather the game has no icon
for: snow, blizzard, each fog strength, and night skies (the game names night icons it doesn't ship).

They copy the look of the game's UI/WeatherIcons, measured from the game's sprites, without using any of
their pixels: one flat colour (125, 123, 111), ~3.3 px strokes at the game's 48 px scale, outlined clouds,
cropped tight to the shape like the game's sprites. Run: python tools/glyphs.py
"""
import math
import os

import numpy as np
from PIL import Image, ImageChops, ImageDraw

S = 8                       # supersampling
COLOUR = (125, 123, 111)
STROKE = 3.3
OUT = os.path.join(os.path.dirname(__file__), "..", "src", "Forecaster", "Glyphs")


def canvas(w, h):
    return Image.new("L", (int(w * S), int(h * S)), 0)


def disc(d, cx, cy, r, fill=255):
    d.ellipse([(cx - r) * S, (cy - r) * S, (cx + r) * S, (cy + r) * S], fill=fill)


def line(d, x0, y0, x1, y1, w=STROKE, fill=255):
    d.line([(x0 * S, y0 * S), (x1 * S, y1 * S)], fill=fill, width=int(round(w * S)))
    disc(d, x0, y0, w / 2, fill)
    disc(d, x1, y1, w / 2, fill)


def bar(d, x0, x1, y, w=STROKE, fill=255):
    """A fog bar: square-ish ends like the game's fog icon."""
    d.rounded_rectangle([x0 * S, (y - w / 2) * S, x1 * S, (y + w / 2) * S], radius=w * S * 0.25, fill=fill)


# The cloud: three bumps on a flat base, as in the game's cloud icons (48 wide, ~33 high).
CLOUD = [(10.5, 20.5, 8.6), (23.5, 13.2, 11.6), (37.0, 18.5, 9.4)]
CLOUD_BASE = (4.0, 18.0, 44.6, 31.0)


def cloud_mask(w, h, ox=0.0, oy=0.0, k=1.0, grow=0.0):
    m = canvas(w, h)
    d = ImageDraw.Draw(m)
    for cx, cy, r in CLOUD:
        disc(d, ox + cx * k, oy + cy * k, r * k + grow)
    x0, y0, x1, y1 = CLOUD_BASE
    rr = (y1 - y0) / 2 * k + grow
    d.rounded_rectangle([(ox + x0 * k - grow) * S, (oy + y0 * k - grow) * S, (ox + x1 * k + grow) * S, (oy + y1 * k + grow) * S], radius=rr * S, fill=255)
    return m


def erode(mask, r):
    """Shrinks a shape by r (in 48-px units) with a round brush, so an outline is even all the way round."""
    a = np.asarray(mask, dtype=np.uint8)
    R = int(round(r * S))
    pad = np.pad(a, R)
    out = np.full_like(a, 255)
    for dy in range(-R, R + 1):
        for dx in range(-R, R + 1):
            if dx * dx + dy * dy <= R * R:
                np.minimum(out, pad[R + dy:R + dy + a.shape[0], R + dx:R + dx + a.shape[1]], out=out)
    return Image.fromarray(out, "L")


def cloud(img, w, h, ox=0.0, oy=0.0, k=1.0, cut=True):
    """Outlined cloud; with cut, whatever was under it (a moon, wind) is cleared first, as the game's icons do."""
    outer = cloud_mask(w, h, ox, oy, k)
    inner = erode(outer, STROKE)
    if cut:
        gap = cloud_mask(w, h, ox, oy, k, grow=1.6)
        img = ImageChops.subtract(img, gap)
    return ImageChops.lighter(img, ImageChops.subtract(outer, inner))


def flake(d, cx, cy, r, w=2.3):
    for a in (90, 30, -30):
        dx, dy = r * math.cos(math.radians(a)), r * math.sin(math.radians(a))
        line(d, cx - dx, cy - dy, cx + dx, cy + dy, w)


def crescent(img, w, h, cx, cy, r):
    outer = canvas(w, h)
    d = ImageDraw.Draw(outer)
    disc(d, cx, cy, r)
    disc(d, cx + r * 0.55, cy - r * 0.42, r * 0.86, 0)
    inner = canvas(w, h)
    d = ImageDraw.Draw(inner)
    disc(d, cx, cy, r - STROKE)
    disc(d, cx + r * 0.55, cy - r * 0.42, r * 0.86 + STROKE, 0)
    return ImageChops.lighter(img, ImageChops.subtract(outer, inner))


def star(d, cx, cy, r):
    line(d, cx - r, cy, cx + r, cy, 1.8)
    line(d, cx, cy - r, cx, cy + r, 1.8)


def save(name, mask):
    mask = mask.resize((mask.width // S, mask.height // S), Image.LANCZOS)
    box = mask.getbbox()
    mask = mask.crop(box)
    img = Image.new("RGBA", mask.size, COLOUR + (0,))
    img.putalpha(mask)
    os.makedirs(OUT, exist_ok=True)
    img.save(os.path.join(OUT, name + ".png"))
    return img


def snow(name, flakes):
    img = canvas(48, 46)
    img = cloud(img, 48, 46)
    d = ImageDraw.Draw(img)
    for cx, cy, r in flakes:
        flake(d, cx, cy, r)
    return save(name, img)


def fog(name, rows, w):
    """A stack of fog bars 30 wide, like the game's fog icon (whose four bars are 3.3 thick, 3 apart)."""
    img = canvas(32, 34)
    d = ImageDraw.Draw(img)
    for y, x0, x1 in rows:
        bar(d, 1 + x0, 1 + x1, 2 + y, w)
    return save(name, img)


def main():
    out = {}
    out["LightSnow"] = snow("LightSnow", [(17, 39.5, 3.6), (31, 39.5, 3.6)])
    out["Snow"] = snow("Snow", [(12, 38.5, 3.4), (24, 41.5, 3.4), (36, 38.5, 3.4)])
    out["HeavySnow"] = snow("HeavySnow", [(9, 37.5, 3.0), (19, 41.5, 3.0), (29, 37.5, 3.0), (39, 41.5, 3.0)])

    # Blizzard: wind streaks on the left (as the game's windy icons), the cloud, snow driven sideways.
    img = canvas(52, 46)
    d = ImageDraw.Draw(img)
    line(d, 2.5, 21, 11, 21)
    line(d, 1.7, 26.5, 9, 26.5)
    line(d, 2.5, 32, 13, 32)
    img = cloud(img, 52, 46, ox=6.5, oy=0, k=0.94)
    d = ImageDraw.Draw(img)
    for cx, cy in ((19, 38.5), (31, 41), (43, 38)):
        flake(d, cx, cy, 3.2)
        line(d, cx - 9.5, cy + 1.6, cx - 5.6, cy + 1.0, 1.7)
    out["Blizzard"] = save("Blizzard", img)

    # Fog family, all 30 wide so they scale alike: mist is thin broken bars, then 4, 5 and 6 full bars.
    out["Mist"] = fog("Mist", [(2, 0, 11), (2, 15, 30), (8, 4, 20), (8, 23, 28), (14, 0, 8), (14, 12, 26)], 2.2)
    # Bar edges on whole pixels so they stay crisp: 3 px bars 5 apart, then 4 px bars 5 apart.
    out["DenseFog"] = fog("DenseFog", [(0.5 + i * 5, 0, 30) for i in range(5)], 3)
    out["ThickFog"] = fog("ThickFog", [(1 + i * 5, 0, 30) for i in range(6)], 4)

    # Night skies: a crescent moon where the day icons have the sun.
    img = canvas(40, 40)
    img = crescent(img, 40, 40, 18, 21, 13)
    d = ImageDraw.Draw(img)
    star(d, 32, 7, 3.2)
    star(d, 36, 18, 2.2)
    out["ClearNight"] = save("ClearNight", img)

    img = canvas(50, 48)
    img = crescent(img, 50, 48, 14, 15, 10)
    img = cloud(img, 50, 48, ox=4, oy=15, k=0.94)
    out["PartlyCloudNight"] = save("PartlyCloudNight", img)

    img = canvas(40, 42)
    img = crescent(img, 40, 42, 17, 15, 11)
    d = ImageDraw.Draw(img)
    for y in (27, 33, 39):
        bar(d, 2, 38, y)
    out["ClearFogNight"] = save("ClearFogNight", img)
    return out


if __name__ == "__main__":
    for name, im in main().items():
        print(name, im.size)
