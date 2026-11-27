#!/usr/bin/env python3
"""PGPM's logo and the PWA's icons (docs/03 §11.2), written into PGPM.WebUI/public/.

    python3 -I tools/make-icons.py

The logo: four cards on a 2x2 matrix (a matrix map's quadrants), the top right one – the "star initiatives" – solid,
the others half-transparent, on the brand blue. Python's standard library only (zlib, struct): the shapes are rounded
rectangles, rasterised exactly per scanline with 4 sub-rows, so no image library and no node are needed. The output is
deterministic; commit what it writes. src/components/LogoMark.vue draws the same cards in currentColor.
"""
import math
import os
import struct
import sys
import zlib

BRAND = (0x3B, 0x59, 0xA5)
WHITE = (0xFF, 0xFF, 0xFF)

# In units of a 512 canvas.
BACKGROUND_RADIUS = 112
GRID = (128, 128, 384, 384)  # the four cards' area: half the side, inside a maskable icon's safe zone (a 40 % circle)
GAP = 32
CARD_RADIUS = 20
DIM = 0.5  # the three other cards' opacity


def cards():
    """(x0, y0, x1, y1, opacity) of the four cards, top right solid."""
    x0, y0, x1, y1 = GRID
    side = (x1 - x0 - GAP) / 2
    out = []
    for row in range(2):
        for col in range(2):
            cx = x0 + col * (side + GAP)
            cy = y0 + row * (side + GAP)
            out.append((cx, cy, cx + side, cy + side, 1.0 if (row, col) == (0, 1) else DIM))
    return out


def span(rect, radius, y):
    """The x interval of a rounded rectangle at height y, or None."""
    x0, y0, x1, y1 = rect
    if y < y0 or y > y1:
        return None
    if radius <= 0:
        return x0, x1
    if y < y0 + radius:
        d = y0 + radius - y
    elif y > y1 - radius:
        d = y - (y1 - radius)
    else:
        return x0, x1
    inset = radius - math.sqrt(max(0.0, radius * radius - d * d))
    return x0 + inset, x1 - inset


def coverage(size, rect, radius, sub=4):
    """Per pixel, the share of it the shape covers: exact horizontally, `sub` rows vertically."""
    rows = []
    for py in range(size):
        acc = [0.0] * size
        for k in range(sub):
            s = span(rect, radius, py + (k + 0.5) / sub)
            if s is None:
                continue
            a, b = s
            for px in range(max(0, int(a)), min(size, int(math.ceil(b)))):
                overlap = min(b, px + 1) - max(a, px)
                if overlap > 0:
                    acc[px] += overlap / sub
        rows.append(acc)
    return rows


def render(size, full_bleed):
    scale = size / 512
    layers = []
    radius = 0 if full_bleed else BACKGROUND_RADIUS * scale
    layers.append((BRAND, 1.0, coverage(size, (0, 0, size, size), radius)))
    for x0, y0, x1, y1, opacity in cards():
        rect = (x0 * scale, y0 * scale, x1 * scale, y1 * scale)
        layers.append((WHITE, opacity, coverage(size, rect, CARD_RADIUS * scale)))
    # "Over" compositing, premultiplied.
    pixels = bytearray()
    for py in range(size):
        pixels.append(0)  # PNG filter: none
        for px in range(size):
            r = g = b = a = 0.0
            for colour, opacity, cov in layers:
                alpha = opacity * cov[py][px]
                if alpha <= 0:
                    continue
                r = colour[0] * alpha + r * (1 - alpha)
                g = colour[1] * alpha + g * (1 - alpha)
                b = colour[2] * alpha + b * (1 - alpha)
                a = alpha + a * (1 - alpha)
            if a > 0:
                pixels += bytes((round(r / a), round(g / a), round(b / a), round(a * 255)))
            else:
                pixels += b"\0\0\0\0"
    return bytes(pixels)


def png(size, raw):
    def chunk(kind, data):
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)  # 8-bit RGBA
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def svg():
    hex_brand = "#%02x%02x%02x" % BRAND
    parts = [
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512">',
        f'<rect width="512" height="512" rx="{BACKGROUND_RADIUS}" fill="{hex_brand}"/>',
    ]
    for x0, y0, x1, y1, opacity in cards():
        fill = '"#fff"' if opacity == 1.0 else f'"#fff" fill-opacity="{opacity}"'
        parts.append(f'<rect x="{x0:g}" y="{y0:g}" width="{x1 - x0:g}" height="{y1 - y0:g}" rx="{CARD_RADIUS}" fill={fill}/>')
    parts.append("</svg>")
    return "".join(parts) + "\n"


def main():
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "PGPM.WebUI", "public")
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, "favicon.svg"), "w", encoding="utf-8") as f:
        f.write(svg())
    for name, size, full_bleed in [
        ("pwa-192.png", 192, False),
        ("pwa-512.png", 512, False),
        # Maskable: the platform cuts its own shape out of a full square; the cards sit in the safe zone.
        ("pwa-maskable-512.png", 512, True),
        # iOS rounds the corners itself and shows transparency as black.
        ("apple-touch-icon.png", 180, True),
    ]:
        with open(os.path.join(out, name), "wb") as f:
            f.write(png(size, render(size, full_bleed)))
        print(f"wrote {name} ({size}x{size})")
    print("wrote favicon.svg")
    return 0


if __name__ == "__main__":
    sys.exit(main())
