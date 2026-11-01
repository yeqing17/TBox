"""Generate the TBox app icon (multi-size .ico)."""
from PIL import Image, ImageDraw
import os

S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# Rounded-square background with vertical gradient (blue -> indigo).
radius = int(S * 0.22)
grad = Image.new("RGBA", (S, S))
gd = ImageDraw.Draw(grad)
top = (59, 130, 246)      # #3B82F6
bottom = (99, 102, 241)   # #6366F1
for y in range(S):
    t = y / (S - 1)
    gd.line([(0, y), (S, y)], fill=(
        int(top[0] + (bottom[0] - top[0]) * t),
        int(top[1] + (bottom[1] - top[1]) * t),
        int(top[2] + (bottom[2] - top[2]) * t),
        255,
    ))

mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=radius, fill=255)
img.paste(grad, (0, 0), mask)

# Subtle top highlight for depth.
gloss = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(gloss).rounded_rectangle(
    [0, 0, S - 1, int(S * 0.55)], radius=radius, fill=(255, 255, 255, 26))
img = Image.alpha_composite(img, gloss)

# 2x2 launcher tiles; top-left solid, others translucent.
cell = int(S * 0.235)
gap = int(S * 0.062)
total = cell * 2 + gap
x0 = (S - total) // 2
y0 = (S - total) // 2
r = int(cell * 0.26)
positions = [(x0, y0), (x0 + cell + gap, y0), (x0, y0 + cell + gap), (x0 + cell + gap, y0 + cell + gap)]
alphas = [255, 150, 150, 190]
for (x, y), a in zip(positions, alphas):
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(layer).rounded_rectangle([x, y, x + cell, y + cell], radius=r, fill=(255, 255, 255, a))
    img = Image.alpha_composite(img, layer)

out_dir = os.path.join(os.path.dirname(__file__), "..", "src", "TBox.WinUI", "Assets")
out_dir = os.path.abspath(out_dir)
os.makedirs(out_dir, exist_ok=True)

sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
ico_path = os.path.join(out_dir, "tbox.ico")
img.resize((256, 256), Image.LANCZOS).save(ico_path, format="ICO", sizes=sizes)

# Also keep a PNG for documentation / store screenshots.
img.resize((512, 512), Image.LANCZOS).save(os.path.join(out_dir, "tbox-512.png"))
print("wrote", ico_path)
