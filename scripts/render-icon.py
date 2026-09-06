"""Regenerates assets/icon-512.png and assets/icon-64.png from the same geometry as assets/VenueOS.svg.
Requires Pillow (`pip install pillow`) — a one-off authoring tool, not a VenueOS runtime dependency; nothing
in the plugin itself depends on Python. Run from the repository root: `python scripts/render-icon.py`.
If you change the design, update assets/VenueOS.svg to match (or vice versa) so the two stay in sync.
"""
from pathlib import Path
from PIL import Image, ImageDraw

SCALE = 4
SIZE = 512 * SCALE
OUT_DIR = Path(__file__).resolve().parent.parent / "assets"

BODY = (26, 32, 46, 255)          # tablet chassis
BODY_BORDER = (58, 68, 92, 255)   # subtle chassis edge
SCREEN = (34, 42, 58, 255)        # inset screen area
TILE = (68, 211, 196, 255)        # teal app tile
TILE_ACTIVE = (245, 176, 65, 255) # amber "active" app tile
TILE_DIM = (52, 96, 94, 255)      # secondary teal tile (depth)

img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
draw = ImageDraw.Draw(img)

margin = 24 * SCALE
body_radius = 96 * SCALE
draw.rounded_rectangle([margin, margin, SIZE - margin, SIZE - margin], radius=body_radius, fill=BODY, outline=BODY_BORDER, width=6 * SCALE)

screen_margin = 72 * SCALE
screen_radius = 64 * SCALE
draw.rounded_rectangle([screen_margin, screen_margin, SIZE - screen_margin, SIZE - screen_margin], radius=screen_radius, fill=SCREEN)

# 2x2 grid of rounded app tiles inside the screen, with generous gutters so it reads clearly at small sizes.
inner_margin = screen_margin + 40 * SCALE
gap = 32 * SCALE
grid_span = (SIZE - 2 * inner_margin - gap) / 2
tile_radius = 28 * SCALE

positions = [
    (inner_margin, inner_margin, TILE),
    (inner_margin + grid_span + gap, inner_margin, TILE_DIM),
    (inner_margin, inner_margin + grid_span + gap, TILE_DIM),
    (inner_margin + grid_span + gap, inner_margin + grid_span + gap, TILE_ACTIVE),
]
for x, y, color in positions:
    draw.rounded_rectangle([x, y, x + grid_span, y + grid_span], radius=tile_radius, fill=color)

# A small notch/indicator on the active tile to suggest "live" without relying on text.
active_x, active_y, _ = positions[3]
dot_r = 14 * SCALE
cx, cy = active_x + grid_span - dot_r - 18 * SCALE, active_y + dot_r + 18 * SCALE
draw.ellipse([cx - dot_r, cy - dot_r, cx + dot_r, cy + dot_r], fill=(26, 32, 46, 255))

final = img.resize((512, 512), Image.LANCZOS)
OUT_DIR.mkdir(exist_ok=True)
final.save(OUT_DIR / "icon-512.png")
final.resize((64, 64), Image.LANCZOS).save(OUT_DIR / "icon-64.png")
print(f"Wrote {OUT_DIR / 'icon-512.png'} and {OUT_DIR / 'icon-64.png'}")
