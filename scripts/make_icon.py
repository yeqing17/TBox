"""Generate the TBox app icon (multi-size .ico) from the logo artwork.

The source (tbox-logo-source.png) is a transparent RGBA mark: two overlapping
black squares with a white T cut-out. Keep the background transparent; the mark
is only cropped, centred and scaled. Saved as a 9-size ICO plus 512px PNGs.
"""
from PIL import Image
import os
import shutil

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, ".."))
SRC = os.path.join(ROOT, "docs", "logo", "tbox-logo-source.png")
ASSETS = os.path.join(ROOT, "src", "TBox.WinUI", "Assets")
DOCS = os.path.join(ROOT, "docs", "images")
LOGO = os.path.join(ROOT, "docs", "logo")

S = 1024
MARK_RATIO = 0.82             # mark size relative to the transparent canvas

src = Image.open(SRC).convert("RGBA")

# Crop to the actual artwork. The source has a large transparent margin and a very faint
# alpha halo (values 1-8); a plain getbbox() includes the halo and makes the mark look
# off-centre. Threshold the alpha first, then add a few pixels for the antialiased edge.
alpha = src.getchannel("A").point(lambda v: 255 if v > 8 else 0)
alpha_bbox = alpha.getbbox()
if alpha_bbox is None:
    raise SystemExit("logo source has no opaque pixels")
pad = 3
alpha_bbox = (
    max(0, alpha_bbox[0] - pad),
    max(0, alpha_bbox[1] - pad),
    min(src.width, alpha_bbox[2] + pad),
    min(src.height, alpha_bbox[3] + pad),
)
mark = src.crop(alpha_bbox)

# Scale the mark to 72% of the tile, preserving its aspect ratio.
target = int(S * MARK_RATIO)
ratio = min(target / mark.width, target / mark.height)
mark = mark.resize((max(1, round(mark.width * ratio)), max(1, round(mark.height * ratio))), Image.LANCZOS)

# Transparent canvas: no tile, no background fill. The white T stays part of the mark.
canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))
canvas.alpha_composite(mark, ((S - mark.width) // 2, (S - mark.height) // 2))

os.makedirs(ASSETS, exist_ok=True)
os.makedirs(DOCS, exist_ok=True)
os.makedirs(LOGO, exist_ok=True)

sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
ico_path = os.path.join(ASSETS, "tbox.ico")
# Save from the 1024 canvas so PIL downsamples every frame from the full artwork;
# pre-resizing to 256 first loses detail at 16/20/24.
canvas.save(ico_path, format="ICO", sizes=sizes)

png = canvas.resize((512, 512), Image.LANCZOS)
png.save(os.path.join(ASSETS, "tbox-512.png"))
png.save(os.path.join(DOCS, "app-icon.png"))

# Keep the generated files in the archive alongside the source, so the design
# originals and the shipped outputs are always in sync.
png.save(os.path.join(LOGO, "tbox-logo-512.png"))
shutil.copyfile(ico_path, os.path.join(LOGO, "tbox-logo.ico"))

print("source crop", alpha_bbox, "mark", mark.size)
print("wrote", ico_path)
print("archived", LOGO)
