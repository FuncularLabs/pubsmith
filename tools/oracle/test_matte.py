"""Tests for matte.py (run: python -m unittest tools/oracle/test_matte.py from the repo root)."""
import os
import subprocess
import sys
import tempfile
import unittest

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
MATTE = os.path.join(HERE, "matte.py")


def composite(rgba, background):
    """Composite an RGBA float array onto a solid background the way Publisher does (straight alpha)."""
    a = rgba[..., 3:4] / 255.0
    return (rgba[..., :3] * a + np.array(background, dtype=np.float64) * (1 - a)).round().astype(np.uint8)


class MatteTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()

    def run_matte(self, element, size=(60, 40)):
        page = np.zeros((size[1], size[0], 4))
        x, y = 10, 5
        h, w = element.shape[:2]
        page[y:y + h, x:x + w] = element
        white, black = composite(page, (255, 255, 255)), composite(page, (0, 0, 0))
        wp, bp, op = (os.path.join(self.dir, n) for n in ("w.png", "b.png", "o.png"))
        Image.fromarray(white).save(wp)
        Image.fromarray(black).save(bp)
        out = subprocess.run([sys.executable, MATTE, wp, bp, op], capture_output=True, text=True, check=True)
        return out.stdout.strip(), op

    def test_recovers_colour_alpha_and_box(self):
        element = np.zeros((10, 20, 4))
        element[..., :3] = (200, 30, 60)
        element[..., 3] = 255
        element[:, :5, 3] = 128                      # left quarter half-transparent
        stdout, out = self.run_matte(element)
        self.assertEqual(stdout, "10 5 20 10 60 40")  # x y w h pageW pageH
        got = np.asarray(Image.open(out), dtype=np.int32)
        self.assertEqual(got.shape, (10, 20, 4))
        self.assertTrue(np.all(np.abs(got[:, 5:, :3] - (200, 30, 60)) <= 1))
        self.assertTrue(np.all(got[:, 5:, 3] == 255))
        self.assertTrue(np.all(np.abs(got[:, :5, 3] - 128) <= 1))
        self.assertTrue(np.all(np.abs(got[:, :5, :3] - (200, 30, 60)) <= 3))   # colour survives half alpha

    def test_white_element_is_not_lost(self):
        # A white element is invisible on the white render; only the black render reveals it.
        element = np.full((4, 4, 4), 255.0)
        stdout, out = self.run_matte(element)
        self.assertEqual(stdout, "10 5 4 4 60 40")
        self.assertTrue(np.all(np.asarray(Image.open(out))[..., 3] == 255))

    def test_nothing_visible_prints_empty(self):
        stdout, _ = self.run_matte(np.zeros((3, 3, 4)))
        self.assertEqual(stdout, "EMPTY")

    def test_size_mismatch_fails(self):
        wp, bp = os.path.join(self.dir, "w.png"), os.path.join(self.dir, "b.png")
        Image.new("RGB", (10, 10), "white").save(wp)
        Image.new("RGB", (11, 10), "black").save(bp)
        res = subprocess.run([sys.executable, MATTE, wp, bp, os.path.join(self.dir, "o.png")], capture_output=True, text=True)
        self.assertNotEqual(res.returncode, 0)


if __name__ == "__main__":
    unittest.main()
