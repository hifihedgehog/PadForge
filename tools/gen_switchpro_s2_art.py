"""Extend the Switch Pro 2D asset set to Switch 2 Pro.

Writes into 2DModels/SWITCH2PRO, which is the Switch 2 Pro's OWN asset
set. It is a copy of the original Pro Controller's sprites over a base
widened by a side gutter, not a shared file: a switch-pro slot must not
render a C button and two grip tiles it has no wire for. Runs before
tools/overlay_positions.py, which reads the base this writes and emits
Switch2ProLayout against it. Rerunning is idempotent: the input
is always the pack's own untouched template, never the shipped base.

Adds the three controls the Switch 2 Pro has and the original does not:
the C Button on the front face, and the GL / GR rear grip buttons shown
as floating tiles in a side margin (the Steam Deck's L4/L5/R4/R5
treatment, which is the repo's established answer for a control with no
front-facing position).

Positions are derived, not eyeballed:
  * C Button comes from the purchased hado Switch 2 Pro mesh
    (3DModels/Switch2Pro/CButton.obj, centroid x=0.00 z=-12.08) mapped
    into the 2D frame by a linear fit over controls present in both, and
    cross-checked against Nintendo's own controller diagram, which puts
    it "on the front face between the D-pad and right stick area".
  * Its size and rounded-square silhouette come from the same mesh:
    CButton and Capture share a 6.28mm footprint and the same corner
    profile (top-face radius spread 1.35 vs 1.32; the round face buttons
    and Home measure 1.00), so C reuses the Capture press sprite.

Palette is sampled from the base render, not guessed:
  body #3A3B40, button outline #211E1E, cap grey #686B6E, glyph white.
"""
import os
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "PadForge.App", "2DModels", "SWITCH2PRO")
PACK = os.path.join(os.path.dirname(ROOT), "Gamepad-Asset-Pack", "Controller Asset Pack",
                    "Nintendo Switch Controller Images", "Switch Pro Controller",
                    "Default Theme", "Templates", "NSwitchPro_base.png")

MARGIN = 160          # side gutter the floating tiles live in
TILE = 130            # GL / GR tile edge
TILE_Y = 735          # top of tile, centred on the grips
SS = 4                # supersample factor for every drawn shape

OUTLINE = (33, 30, 30, 255)
CAP = (104, 107, 110, 255)
GLYPH = (255, 255, 255, 255)
RING = (36, 210, 246, 255)        # press-sprite outline
RING_FILL = (16, 120, 147, 128)   # press-sprite interior

# C Button, in the ORIGINAL 1485-wide frame. See module docstring.
C_W, C_H = 62, 63
C_CX, C_CY = 742.5, 656.0


def font(size):
    # The pack's A/B/X/Y glyphs are a regular-weight geometric sans, not a
    # light one. A light face renders grey after antialiasing and reads as
    # a different family beside them.
    for name in ("segoeui.ttf", "arial.ttf"):
        p = os.path.join(r"C:\Windows\Fonts", name)
        if os.path.exists(p):
            return ImageFont.truetype(p, size)
    raise SystemExit("no usable font")


def centred_text(draw, box, text, fnt, fill):
    """Draw text optically centred in box (x0,y0,x1,y1) by its ink bbox."""
    x0, y0, x1, y1 = box
    bb = draw.textbbox((0, 0), text, font=fnt)
    tw, th = bb[2] - bb[0], bb[3] - bb[1]
    draw.text((x0 + (x1 - x0 - tw) / 2 - bb[0],
               y0 + (y1 - y0 - th) / 2 - bb[1]), text, font=fnt, fill=fill)


def rounded(draw, box, radius, fill=None, outline=None, width=0):
    draw.rounded_rectangle(box, radius=radius, fill=fill, outline=outline, width=width)


def draw_c_button(img, cx, cy):
    """The C Button, drawn in the Capture button's exact treatment."""
    lay = Image.new("RGBA", (img.width * SS, img.height * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(lay)
    hw, hh = C_W * SS / 2, C_H * SS / 2
    x0, y0 = cx * SS - hw, cy * SS - hh
    x1, y1 = cx * SS + hw, cy * SS + hh
    r = 12 * SS
    rounded(d, (x0, y0, x1, y1), r, fill=OUTLINE)                 # dark rim
    inset = 6 * SS
    rounded(d, (x0 + inset, y0 + inset, x1 - inset, y1 - inset),
            r - inset * 0.55, fill=CAP)                            # grey cap
    centred_text(d, (x0 + inset, y0 + inset, x1 - inset, y1 - inset),
                 "C", font(int(34 * SS)), GLYPH)
    lay = lay.resize((img.width, img.height), Image.LANCZOS)
    img.alpha_composite(lay)


def draw_tile(img, x0, y0, label):
    """A floating GL / GR tile: same rim + cap + white glyph language."""
    lay = Image.new("RGBA", (img.width * SS, img.height * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(lay)
    X0, Y0 = x0 * SS, y0 * SS
    X1, Y1 = (x0 + TILE) * SS, (y0 + TILE) * SS
    r = 20 * SS
    rounded(d, (X0, Y0, X1, Y1), r, fill=OUTLINE)
    inset = 5 * SS
    rounded(d, (X0 + inset, Y0 + inset, X1 - inset, Y1 - inset),
            r - inset * 0.55, fill=CAP)
    centred_text(d, (X0 + inset, Y0 + inset, X1 - inset, Y1 - inset),
                 label, font(int(52 * SS)), GLYPH)
    lay = lay.resize((img.width, img.height), Image.LANCZOS)
    img.alpha_composite(lay)


def make_tile_sprite(path):
    """Press sprite for a grip tile: the family's cyan rim + wash."""
    lay = Image.new("RGBA", (TILE * SS, TILE * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(lay)
    r = 20 * SS
    rounded(d, (0, 0, TILE * SS - 1, TILE * SS - 1), r, fill=RING_FILL)
    rounded(d, (0, 0, TILE * SS - 1, TILE * SS - 1), r, outline=RING, width=6 * SS)
    lay.resize((TILE, TILE), Image.LANCZOS).save(path)
    print(f"  wrote {os.path.basename(path)} {TILE}x{TILE}")


def main():
    src = Image.open(PACK).convert("RGBA")
    print(f"pack base {src.size}")
    if src.size != (1485, 1079):
        raise SystemExit(f"unexpected pack base size {src.size}")

    out = Image.new("RGBA", (src.width + 2 * MARGIN, src.height), (0, 0, 0, 0))
    out.alpha_composite(src, (MARGIN, 0))

    draw_c_button(out, C_CX + MARGIN, C_CY)
    draw_tile(out, (MARGIN - TILE) // 2, TILE_Y, "GL")
    draw_tile(out, out.width - (MARGIN - TILE) // 2 - TILE, TILE_Y, "GR")

    dst = os.path.join(SRC, "NSwitchPro_base.png")
    out.save(dst)
    print(f"  wrote base {out.size} -> {dst}")

    make_tile_sprite(os.path.join(SRC, "NSwitchPro_GripTile.png"))

    # Emitted layout geometry, in the NEW frame.
    print("\nlayout geometry (new frame):")
    print(f"  BaseWidth  = {out.width}")
    print(f"  BaseHeight = {out.height}")
    print(f"  C   x={round(C_CX + MARGIN - C_W / 2)} y={round(C_CY - C_H / 2)} w={C_W} h={C_H}")
    print(f"  GL  x={(MARGIN - TILE)//2} y={TILE_Y} w={TILE} h={TILE}")
    print(f"  GR  x={out.width - (MARGIN - TILE)//2 - TILE} y={TILE_Y} w={TILE} h={TILE}")
    print(f"  X shift for every existing element = +{MARGIN}")


main()
