"""Draws the neutral test art in tests/fixtures/assets (and the corpus art folder): procedural shapes only.

    python make-sample-art.py <out-dir>

sample-picture.png  1024x1024 opaque: radial gradient, quadrant marks and simple shapes (for crop, mask and
                    placement tests; each corner looks different).
sample-strip.png    480x146 transparent: five teardrops, the last one outline only (for alpha tests).
Needs Pillow.
"""
import math
import pathlib
import sys

from PIL import Image, ImageDraw

out = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else ".")
out.mkdir(parents=True, exist_ok=True)

W = 1024
img = Image.new("RGB", (W, W))
px = img.load()
for y in range(W):
    for x in range(W):
        d = math.hypot(x - W / 2, y - W / 2) / (W / 2)
        px[x, y] = (int(40 + 200 * (1 - min(d, 1))), int(90 + 120 * abs(math.sin(x / 90))), int(170 + 70 * abs(math.cos(y / 110))))
d = ImageDraw.Draw(img)
d.ellipse((312, 312, 712, 712), fill=(250, 200, 40), outline=(30, 30, 30), width=12)
d.rectangle((80, 80, 260, 260), fill=(220, 40, 60))
d.polygon([(860, 80), (950, 260), (770, 260)], fill=(40, 170, 90))
d.rectangle((770, 770, 950, 950), outline=(255, 255, 255), width=16)
for i in range(8):
    d.line((0, 128 * i, W, 128 * i + 64), fill=(255, 255, 255), width=3)
img.save(out / "sample-picture.png")

s = Image.new("RGBA", (480, 146), (0, 0, 0, 0))
d = ImageDraw.Draw(s)
for i in range(5):
    x0 = 20 + i * 92
    shape = [(x0 + 40, 10), (x0 + 70, 70), (x0 + 55, 130), (x0 + 25, 130), (x0 + 10, 70)]
    if i < 4:
        d.polygon(shape, fill=(30, 120, 200, 255))
    else:
        d.polygon(shape, outline=(30, 120, 200, 255), width=5)
s.save(out / "sample-strip.png")
print("wrote", out / "sample-picture.png", out / "sample-strip.png")
