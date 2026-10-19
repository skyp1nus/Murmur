#!/usr/bin/env python3
"""Regenerates installer/murmur.ico: the recording "orb" of the design (docs/design/source/Recording.dc.html).

The orb is four stacked CSS radial gradients on a lavender base:
    radial-gradient(circle at 32% 26%, #fff1e8 0%, transparent 30%)   highlight
    radial-gradient(circle at 74% 70%, #0447ff 0%, transparent 58%)   accentViolet
    radial-gradient(circle at 20% 80%, #ff4704 0%, transparent 55%)   accentEmber
    radial-gradient(circle at 72% 20%, #f4a6c8 0%, transparent 50%)   pink
    #d8c8f0                                                            base
This script draws the same thing (CSS "farthest-corner" radius, first layer on top) at 4x and downsamples, once per
icon size, then packs the PNGs into a Windows .ico (PNG-compressed entries, Vista and later).

Needs: python3, numpy, Pillow.   Usage: python3 installer/make-icon.py [output.ico]
"""
import io
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image

SIZES = (16, 24, 32, 48, 64, 128, 256)
SUPERSAMPLE = 4
FILL = 0.94  # the orb diameter as a share of the canvas; the rest is a transparent margin
BASE = (0xD8, 0xC8, 0xF0)
# (centre x, centre y, colour, transparent-at) from top layer to bottom layer.
LAYERS = (
    (0.32, 0.26, (0xFF, 0xF1, 0xE8), 0.30),
    (0.74, 0.70, (0x04, 0x47, 0xFF), 0.58),
    (0.20, 0.80, (0xFF, 0x47, 0x04), 0.55),
    (0.72, 0.20, (0xF4, 0xA6, 0xC8), 0.50),
)


def render(size: int) -> Image.Image:
    n = size * SUPERSAMPLE
    ys, xs = np.mgrid[0:n, 0:n]
    px = (xs + 0.5) / n
    py = (ys + 0.5) / n

    # Orb box inside the canvas (unit square coordinates of the gradients refer to this box).
    margin = (1.0 - FILL) / 2.0
    bx = (px - margin) / FILL
    by = (py - margin) / FILL

    rgb = np.empty((n, n, 3), dtype=np.float64)
    rgb[:] = BASE
    # Paint from the bottom layer up so that the first CSS layer ends up on top.
    for cx, cy, colour, stop in reversed(LAYERS):
        # farthest-corner radius of a unit box for a circle centred at (cx, cy)
        radius = np.hypot(max(cx, 1 - cx), max(cy, 1 - cy))
        t = np.hypot(bx - cx, by - cy) / radius
        alpha = np.clip(1.0 - t / stop, 0.0, 1.0)[..., None]
        rgb = rgb * (1.0 - alpha) + np.array(colour, dtype=np.float64) * alpha

    inside = np.hypot(bx - 0.5, by - 0.5) <= 0.5
    # Premultiplied downsample so the transparent margin does not tint the edge.
    a = inside.astype(np.float64)[..., None]
    pre = np.concatenate([rgb * a, a], axis=-1)
    pre = pre.reshape(size, SUPERSAMPLE, size, SUPERSAMPLE, 4).mean(axis=(1, 3))
    alpha = pre[..., 3:4]
    colour = np.where(alpha > 0, pre[..., :3] / np.maximum(alpha, 1e-9), 0.0)
    out = np.concatenate([colour, alpha * 255.0], axis=-1)
    return Image.fromarray(np.clip(np.rint(out), 0, 255).astype(np.uint8), 'RGBA')


def pack_ico(images: list[Image.Image]) -> bytes:
    blobs = []
    for img in images:
        buf = io.BytesIO()
        img.save(buf, format='PNG', optimize=True)
        blobs.append(buf.getvalue())
    header = struct.pack('<HHH', 0, 1, len(blobs))
    offset = 6 + 16 * len(blobs)
    entries = b''
    for img, blob in zip(images, blobs):
        w = img.width if img.width < 256 else 0  # 0 means 256 in an ICO directory entry
        entries += struct.pack('<BBBBHHII', w, w, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    return header + entries + b''.join(blobs)


def main() -> None:
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).with_name('murmur.ico')
    data = pack_ico([render(s) for s in SIZES])
    out.write_bytes(data)
    print(f'{out}: {len(data)} bytes, sizes {", ".join(map(str, SIZES))}')


if __name__ == '__main__':
    main()
