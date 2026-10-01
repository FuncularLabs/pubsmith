"""Difference matting: recover an element's exact colour + alpha from two renders of the same page,
one on white and one on black (Publisher composites normally, so W = a*C + (1-a)*255 and B = a*C).

usage: python matte.py <on-white.png> <on-black.png> <out.png>
Writes the element cropped to its visible pixels as RGBA and prints "x y w h W H": the crop box in pixels of
the page image, then the page image's width and height. Prints "EMPTY" when nothing is visible.
"""
import sys

import numpy as np
from PIL import Image


def main(white_path, black_path, out_path):
    w = np.asarray(Image.open(white_path).convert("RGB"), dtype=np.float64)
    b = np.asarray(Image.open(black_path).convert("RGB"), dtype=np.float64)
    if w.shape != b.shape:
        raise SystemExit(f"size mismatch {w.shape} vs {b.shape}")
    alpha = np.clip(1.0 - (w - b).mean(axis=2) / 255.0, 0.0, 1.0)
    visible = alpha > 0.5 / 255
    if not visible.any():
        print("EMPTY")
        return
    rows = np.flatnonzero(visible.any(axis=1))
    cols = np.flatnonzero(visible.any(axis=0))
    y0, y1, x0, x1 = rows[0], rows[-1] + 1, cols[0], cols[-1] + 1
    a = alpha[y0:y1, x0:x1]
    safe = np.where(a > 0, a, 1.0)[..., None]
    colour = np.clip(b[y0:y1, x0:x1] / safe, 0, 255)
    rgba = np.dstack([colour, a[..., None] * 255.0]).round().astype(np.uint8)
    Image.fromarray(rgba, "RGBA").save(out_path)
    print(x0, y0, x1 - x0, y1 - y0, w.shape[1], w.shape[0])


if __name__ == "__main__":
    main(*sys.argv[1:4])
