"""Builds the player name tag icon: Content/Textures/GUI/Icons/SE2Char/Engineer.png.

SE2's HUD icons are DDS files (DX10 header, R8G8B8A8_UNORM_SRGB, full mip chain) despite the .png name,
matching UI/HUD/GPS/*.png in the game content. Usage: python make_engineer_icon.py <output.png> [preview.png]
"""
import math
import struct
import sys

from PIL import Image, ImageDraw

W, H, S = 469, 514, 4  # stock GPS icon size, 4x supersampling
BORDER = (86, 196, 178, 255)
FILL = (32, 92, 92, 255)
GLYPH = (255, 255, 255, 255)


def hexagon(cx, cy, rx, ry, corner):
    """Pointy-top hexagon with rounded corners, as a polygon."""
    corners = [(cx + rx * math.cos(math.radians(a)), cy + ry * math.sin(math.radians(a))) for a in range(-90, 270, 60)]
    points = []
    for i, (x, y) in enumerate(corners):
        px, py = corners[i - 1]
        nx, ny = corners[(i + 1) % 6]
        for t in range(9):
            # quadratic bezier through the corner between the two edge points `corner` away from it
            a = t / 8
            ax, ay = x + (px - x) * corner, y + (py - y) * corner
            bx, by = x + (nx - x) * corner, y + (ny - y) * corner
            points.append(((1 - a) ** 2 * ax + 2 * (1 - a) * a * x + a * a * bx, (1 - a) ** 2 * ay + 2 * (1 - a) * a * y + a * a * by))
    return [(px * S, py * S) for px, py in points]


def s(*v):
    return [x * S for x in v]


def draw():
    img = Image.new("RGBA", (W * S, H * S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    cx, cy = W / 2, H / 2
    d.polygon(hexagon(cx, cy, 268, 255, 0.16), fill=BORDER)
    d.polygon(hexagon(cx, cy, 242, 232, 0.16), fill=FILL)
    # shoulders and chest plate
    d.rounded_rectangle(s(118, 338, 351, 440), radius=60 * S, fill=GLYPH)
    d.rectangle(s(118, 400, 351, 440), fill=GLYPH)
    # neck ring, separated from the shoulders by a fill gap
    d.rounded_rectangle(s(186, 318, 283, 352), radius=14 * S, fill=FILL)
    d.rounded_rectangle(s(194, 312, 275, 340), radius=12 * S, fill=GLYPH)
    # helmet dome
    d.ellipse(s(132, 118, 337, 330), fill=GLYPH)
    # side lamps
    d.rounded_rectangle(s(116, 196, 142, 262), radius=10 * S, fill=GLYPH)
    d.rounded_rectangle(s(327, 196, 353, 262), radius=10 * S, fill=GLYPH)
    # visor cut-out with a glare stripe
    d.rounded_rectangle(s(162, 182, 307, 268), radius=36 * S, fill=FILL)
    d.line(s(186, 236, 214, 204), fill=(255, 255, 255, 150), width=9 * S)
    # chest light
    d.rounded_rectangle(s(214, 372, 255, 386), radius=7 * S, fill=FILL)
    # clip everything to the hexagon outline so the shoulders sit inside the border
    mask = Image.new("L", img.size, 0)
    ImageDraw.Draw(mask).polygon(hexagon(cx, cy, 268, 255, 0.16), fill=255)
    inner = Image.new("L", img.size, 0)
    ImageDraw.Draw(inner).polygon(hexagon(cx, cy, 242, 232, 0.16), fill=255)
    border = Image.new("RGBA", img.size, BORDER)
    out = Image.composite(img, border, inner)
    out.putalpha(mask)
    return out.resize((W, H), Image.LANCZOS)


def dds(image):
    mips = [image]
    while max(mips[-1].size) > 1:
        w, h = mips[-1].size
        mips.append(mips[-1].resize((max(1, w // 2), max(1, h // 2)), Image.LANCZOS))
    w, h = image.size
    header = struct.pack("<4sIIIIIII44x", b"DDS ", 124, 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x8, h, w, w * 4, 0, len(mips))
    header += struct.pack("<II4sIIIII", 32, 0x4, b"DX10", 0, 0, 0, 0, 0)
    header += struct.pack("<IIIII", 0x1000 | 0x400000 | 0x8, 0, 0, 0, 0)
    header += struct.pack("<IIIII", 29, 3, 0, 1, 0)  # R8G8B8A8_UNORM_SRGB, TEXTURE2D, array size 1
    # straight alpha, like the stock icons
    return header + b"".join(m.tobytes() for m in mips)


if __name__ == "__main__":
    icon = draw()
    with open(sys.argv[1], "wb") as f:
        f.write(dds(icon))
    if len(sys.argv) > 2:
        preview = Image.new("RGBA", icon.size, (40, 60, 80, 255))
        preview.alpha_composite(icon)
        preview.convert("RGB").save(sys.argv[2])
