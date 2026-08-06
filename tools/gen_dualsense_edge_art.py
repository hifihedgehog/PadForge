"""Extend the DualSense 2D asset set to the DualSense Edge.

Writes into 2DModels/DUALSENSEEDGE, which is the Edge's OWN asset set. It is
a copy of the DualSense sprites over a base widened by a side gutter, plus a
tile sprite. Kept separate rather than shared for the same reason the Switch 2
Pro set is: a plain DualSense must not render four controls it does not have.

WHY ALL FOUR EXTRAS ARE FLOATING TILES, including the front Fn pair.

The back buttons are on the rear, so no front-facing position exists for them
at all. The Fn buttons DO sit on the front face, below each stick, and the
first attempt was to draw them there. It does not survive measurement:

  * The Edge 3D mesh pins the Fn caps to the HOUSING UNDERSIDE (they face
    down-forward at z=-19, below the pack render's front silhouette), so
    projecting the mesh centroid, the route used for the Switch 2 Pro's C
    button, lands them off this render's body. The 3D view registers the
    real meshes instead; only this 2D render needs tiles.
  * dualsense-tester's Edge front SVG does carry them (LFnPattern /
    RFnPattern, directly below each stick). Fitting that drawing onto this one
    with a per-axis linear fit over thirteen shared controls lands the face
    buttons, D-pad and Create/Option within ~10 px, but the STICKS come out
    51 px off, and the residual grows downward: this art is a 3/4 view that
    foreshortens the lower face, and the reference is near top-down. The fit
    puts the Fn buttons at y=723 on a body whose column ends at y=696, i.e.
    off the pad entirely, in exactly the region where the fit is provably
    unreliable.

So the front position is not derivable from anything on disk, and inventing
one by eye is the thing this pipeline exists to prevent. All four take the
Steam Deck L4/L5/R4/R5 treatment instead, which is the repo's established
answer for a control with no trustworthy front position, and which the Switch
2 Pro's GL/GR already follow.

Palette is sampled from the base render, not guessed: body/cap #E1E3E6,
outline #707373, dark #17191E. Press sprites across this pack share one
treatment (cyan ring #24D3F7 over a #107893 wash at alpha 128), so the tile's
press art matches every other control's.
"""
import os
import shutil
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC_DIR = os.path.join(ROOT, "PadForge.App", "2DModels", "DualSense")
DST_DIR = os.path.join(ROOT, "PadForge.App", "2DModels", "DUALSENSEEDGE")
PACK = os.path.join(os.path.dirname(ROOT), "Gamepad-Asset-Pack",
                    "Controller Asset Pack", "DualSense Controller Image",
                    "Default", "Templates", "White")

MARGIN = 175      # side gutter the floating tiles live in
TILE = 145        # tile edge
GAP = 26          # vertical gap between a side's two tiles
SS = 4            # supersample factor

OUTLINE = (112, 115, 115, 255)
CAP = (225, 227, 230, 255)
GLYPH = (23, 25, 30, 255)
RING = (36, 211, 247, 255)
RING_FILL = (16, 120, 147, 128)


def font(size):
    for name in ("segoeui.ttf", "arial.ttf"):
        p = os.path.join(r"C:\Windows\Fonts", name)
        if os.path.exists(p):
            return ImageFont.truetype(p, size)
    raise SystemExit("no usable font")


def centred_text(draw, box, text, fnt, fill):
    x0, y0, x1, y1 = box
    bb = draw.textbbox((0, 0), text, font=fnt)
    tw, th = bb[2] - bb[0], bb[3] - bb[1]
    draw.text((x0 + (x1 - x0 - tw) / 2 - bb[0],
               y0 + (y1 - y0 - th) / 2 - bb[1]), text, font=fnt, fill=fill)


def draw_tile(img, x0, y0, label):
    """One floating tile in the base art's own button language: outline rim,
    light cap, dark glyph."""
    lay = Image.new("RGBA", (img.width * SS, img.height * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(lay)
    X0, Y0 = x0 * SS, y0 * SS
    X1, Y1 = (x0 + TILE) * SS, (y0 + TILE) * SS
    r = 22 * SS
    d.rounded_rectangle((X0, Y0, X1, Y1), radius=r, fill=OUTLINE)
    inset = 5 * SS
    d.rounded_rectangle((X0 + inset, Y0 + inset, X1 - inset, Y1 - inset),
                        radius=r - inset * 0.55, fill=CAP)
    centred_text(d, (X0 + inset, Y0 + inset, X1 - inset, Y1 - inset),
                 label, font(int(38 * SS)), GLYPH)
    img.alpha_composite(lay.resize((img.width, img.height), Image.LANCZOS))


def make_tile_sprite(path):
    """Press sprite: the pack's cyan rim + wash, in the tile's silhouette."""
    lay = Image.new("RGBA", (TILE * SS, TILE * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(lay)
    r = 22 * SS
    box = (0, 0, TILE * SS - 1, TILE * SS - 1)
    d.rounded_rectangle(box, radius=r, fill=RING_FILL)
    d.rounded_rectangle(box, radius=r, outline=RING, width=6 * SS)
    lay.resize((TILE, TILE), Image.LANCZOS).save(path)
    print("  wrote %s %dx%d" % (os.path.basename(path), TILE, TILE))


def widen_with_tiles(src):
    """The Edge base construction: the plain-body render over a canvas
    widened by MARGIN each side, with the four labelled tiles in the
    gutter. Shared with tools/gen_2d_colorways.py, which derives the
    Edge's colorway bases from the DualSense colorway bases through this
    exact function so tile placement can never drift between them."""
    if src.size != (1467, 816):
        raise SystemExit("unexpected DualSense base size %s" % (src.size,))
    out = Image.new("RGBA", (src.width + 2 * MARGIN, src.height), (0, 0, 0, 0))
    out.alpha_composite(src, (MARGIN, 0))
    body_top, body_bottom = 85, 815
    block = TILE * 2 + GAP
    top = (body_top + body_bottom) // 2 - block // 2
    tile_x = (MARGIN - TILE) // 2
    right_x = out.width - tile_x - TILE
    placements = [
        ("LeftBack", tile_x, top, "L Back"),
        ("LeftFn", tile_x, top + TILE + GAP, "L Fn"),
        ("RightBack", right_x, top, "R Back"),
        ("RightFn", right_x, top + TILE + GAP, "R Fn"),
    ]
    for _, x, y, label in placements:
        draw_tile(out, x, y, label)
    return out, placements


def main():
    os.makedirs(DST_DIR, exist_ok=True)

    # Sprites come from the shipped DualSense set so the two stay identical.
    # Only the base and the tile sprite are Edge-specific.
    n = 0
    for f in sorted(os.listdir(SRC_DIR)):
        if not f.lower().endswith(".png") or f == "DualSense_base.png":
            continue
        shutil.copyfile(os.path.join(SRC_DIR, f), os.path.join(DST_DIR, f))
        n += 1
    print("  copied %d sprites" % n)

    src = Image.open(os.path.join(SRC_DIR, "DualSense_base.png")).convert("RGBA")

    # Two tiles per side, stacked and vertically centred on the body. Back
    # above Fn, matching the mapping grid's own row order.
    out, placements = widen_with_tiles(src)

    dst = os.path.join(DST_DIR, "DualSense_base.png")
    out.save(dst)
    print("  wrote base %s -> %s" % (out.size, dst))
    make_tile_sprite(os.path.join(DST_DIR, "DualSense_EdgeTile.png"))

    print("\nlayout geometry (new frame):")
    print("  BaseWidth  = %d" % out.width)
    print("  BaseHeight = %d" % out.height)
    for name, x, y, _ in placements:
        print("  %-10s x=%d y=%d w=%d h=%d" % (name, x, y, TILE, TILE))
    print("  X shift for every existing element = +%d" % MARGIN)


if __name__ == "__main__":
    main()
