"""Generates NextcloudCompanion/app.ico: a smiling face on a blue circle.
The eyes are Nextcloud-style white rings; the smile is the "companion".
Requires Pillow.  Usage: python tools/make-icon.py"""
import math
import os
from PIL import Image, ImageDraw

S = 2048                      # supersampled canvas
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
TOP_RIGHT = (0x1A, 0xA7, 0xFD)
BOTTOM_LEFT = (0x05, 0x84, 0xD0)

def gradient(size):
    # diagonal gradient, bottom-left -> top-right
    img = Image.new("RGB", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x + (size - 1 - y)) / (2 * (size - 1))
            px[x, y] = tuple(round(BOTTOM_LEFT[i] + (TOP_RIGHT[i] - BOTTOM_LEFT[i]) * t) for i in range(3))
    return img

def master():
    # Background: blue circle with the Nextcloud-style diagonal gradient.
    bg = gradient(256).resize((S, S), Image.BICUBIC).convert("RGBA")
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).ellipse((0, 0, S - 1, S - 1), fill=255)
    bg.putalpha(mask)

    k = S / 1024
    cx = cy = S / 2
    white = (255, 255, 255, 255)
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)

    # Eyes: Nextcloud-style rings (white ring, open centre).
    for sign in (-1, 1):
        x, y = cx + sign * 215 * k, cy - 130 * k
        d.ellipse((x - 130 * k, y - 130 * k, x + 130 * k, y + 130 * k), fill=white)
        d.ellipse((x - 54 * k, y - 54 * k, x + 54 * k, y + 54 * k), fill=(0, 0, 0, 0))

    # Smile: a thick arc with round ends, drawn as a chain of discs along the centre line.
    r, w = 300 * k, 76 * k
    mx, my = cx, cy - 55 * k
    steps = 400
    for i in range(steps + 1):
        a = math.radians(30 + (150 - 30) * i / steps)
        x, y = mx + r * math.cos(a), my + r * math.sin(a)
        d.ellipse((x - w / 2, y - w / 2, x + w / 2, y + w / 2), fill=white)

    return Image.alpha_composite(bg, layer)

if __name__ == "__main__":
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "NextcloudCompanion", "app.ico")
    m = master()
    frames = [m.resize((n, n), Image.LANCZOS) for n in SIZES]
    frames[-1].save(out, format="ICO", sizes=[(n, n) for n in SIZES], append_images=frames[:-1])
    print("wrote", os.path.normpath(out))
