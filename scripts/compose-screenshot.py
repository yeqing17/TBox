"""Composite a PrintWindow capture onto a neutral, rounded/shadowed background.

Usage:
    python -I scripts/compose-screenshot.py raw.png out.png <radius> <pad> <bg>

Capture with TBOX_OPAQUE=1 so PrintWindow does not sample the desktop/acrylic behind it.
"""
import sys
from PIL import Image, ImageDraw, ImageFilter

raw, out, radius, pad, bg = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4]), sys.argv[5]
scale = 4  # supersample for anti-aliased corners

panel = Image.open(raw).convert("RGBA")
w, h = panel.size

mask = Image.new("L", (w*scale, h*scale), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, w*scale-1, h*scale-1], radius=radius*scale, fill=255)
mask = mask.resize((w, h), Image.LANCZOS)

body = Image.new("RGBA", (w, h), (0, 0, 0, 0))
body.paste(panel, (0, 0), mask)

canvas = Image.new("RGBA", (w + pad*2, h + pad*2 + 8), bg)

shadow = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
sd = ImageDraw.Draw(shadow)
sd.rounded_rectangle([pad, pad+6, pad+w, pad+h+6], radius=radius, fill=(0, 0, 0, 46))
shadow = shadow.filter(ImageFilter.GaussianBlur(10))
canvas = Image.alpha_composite(canvas, shadow)

canvas.alpha_composite(body, (pad, pad))
canvas.convert("RGB").save(out, quality=95)
print(f"{out}  {canvas.size[0]}x{canvas.size[1]}")
