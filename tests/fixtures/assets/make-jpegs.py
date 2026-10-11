"""Makes the two JPEG fixtures SkiaSharp cannot encode: a progressive one and a CMYK one (WI-006's JPEG tests).

Random pixels from a fixed seed; Pillow 12.2.0. Run from this folder: python make-jpegs.py
"""
import random

from PIL import Image

rng = random.Random(6006)
w, h = 48, 32

rgb = Image.new("RGB", (w, h))
rgb.putdata([(rng.randrange(256), rng.randrange(256), rng.randrange(256)) for _ in range(w * h)])
rgb.save("progressive.jpg", "JPEG", quality=85, progressive=True)   # JFIF, YCbCr, progressive Huffman (SOF2)

cmyk = Image.new("CMYK", (w, h))
cmyk.putdata([tuple(rng.randrange(256) for _ in range(4)) for _ in range(w * h)])
cmyk.save("cmyk.jpg", "JPEG", quality=85)   # four components, with an Adobe marker
