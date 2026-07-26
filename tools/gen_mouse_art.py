"""Render the vendored mouse SVG into the layers the preview composites.

WHY IMAGES AND NOT PATHS. The first version of this traced each control into a
polygon with approxPolyDP and had the view stroke it. That was wrong twice: a
22-point polygon is a visibly faceted stand-in for a smooth Bezier, and stroking
each region while ALSO drawing the art's own line-work put a border around every
border. So nothing is re-drawn now. The art is rasterised once, as authored, and
each control is a full-canvas ALPHA MASK over it, which is the same technique the
controller previews already use for their 2DModels overlays.

Every layer shares one canvas, so the view composites them all at (0,0) with no
arithmetic and nothing to desynchronise.

Emits PNG layers into PadForge.App/2DModels/MOUSE/ and a small C# descriptor.
"""
import re, sys, os, cairosvg, cv2, numpy as np

SRC = sys.argv[1] if len(sys.argv) > 1 else "PadForge.App/2DModels/MOUSE/mouse.svg"
OUTDIR = sys.argv[2] if len(sys.argv) > 2 else "PadForge.App/2DModels/MOUSE"
CS = sys.argv[3] if len(sys.argv) > 3 else "PadForge.App/Views/MouseArt.g.cs"

RW = 848                                     # 4x the art's own width; ample for the pane
svg = open(SRC, encoding="utf-8").read()
vb = re.search(r'viewBox="([\d.\-\s]+)"', svg).group(1).split()
VW, VH = float(vb[2]), float(vb[3])

# The line-work, as authored, on transparency. Its ALPHA is the artwork: the
# strokes are white, so the view can tint it to either theme by using this as a
# mask rather than as a picture.
cairosvg.svg2png(url=SRC, write_to=os.path.join(OUTDIR, "mouse_line.png"), output_width=RW)
line = cv2.imread(os.path.join(OUTDIR, "mouse_line.png"), cv2.IMREAD_UNCHANGED)
RH = line.shape[0]
print(f"line-work {RW}x{RH}")

ink = (line[:, :, 3] > 40).astype(np.uint8)
sealed = cv2.morphologyEx(ink, cv2.MORPH_CLOSE, np.ones((3, 3), np.uint8))
n, lab, stats, cent = cv2.connectedComponentsWithStats((1 - sealed).astype(np.uint8), 4)

regs = []
for i in range(1, n):
    x, y, w, h, a = stats[i]
    if a < RW * RH * 0.0006:
        continue
    if x <= 1 and y <= 1 and w > RW * 0.9:            # page outside the shell
        continue
    regs.append(dict(i=i, x=x, y=y, w=w, h=h, a=a, cx=cent[i][0], cy=cent[i][1]))
regs.sort(key=lambda r: -r["a"])
print(f"{len(regs)} enclosed regions")


def take(pred, many=False):
    hits = [r for r in regs if not r.get("used") and pred(r)]
    hits.sort(key=lambda r: -r["a"])
    if not hits:
        return [] if many else None
    if many:
        for h in hits:
            h["used"] = True
        return hits
    hits[0]["used"] = True
    return hits[0]


palm = take(lambda r: True)
lmb = take(lambda r: r["cx"] < RW * .45 and r["cy"] < RH * .45 and r["a"] > RW * RH * .02)
rmb = take(lambda r: r["cx"] > RW * .55 and r["cy"] < RH * .45 and r["a"] > RW * RH * .02)
whl = take(lambda r: abs(r["cx"] - RW / 2) < RW * .10 and r["cy"] < RH * .45)
sides = take(lambda r: r["cx"] < RW * .22, many=True)
sides.sort(key=lambda r: r["cy"])
vents = take(lambda r: True, many=True)

layers = {}
if palm:
    layers["Body"] = [palm] + vents          # one shell-fill mask
if lmb:
    layers["Lmb"] = [lmb]
if rmb:
    layers["Rmb"] = [rmb]
if whl:
    layers["Wheel"] = [whl]
if len(sides) > 0:
    layers["SideUpper"] = [sides[0]]
if len(sides) > 1:
    layers["SideLower"] = [sides[1]]


def write_mask(name, members):
    m = np.zeros((RH, RW), np.uint8)
    for r in members:
        m[lab == r["i"]] = 255
    # Grow back over the seam the flood fill stopped at, so a lit control meets
    # its own outline instead of leaving a dark halo, then soften by a pixel so
    # the edge is not a hard staircase when the Viewbox scales it up.
    m = cv2.dilate(m, np.ones((3, 3), np.uint8), iterations=2)
    m = cv2.GaussianBlur(m, (3, 3), 0)
    ys, xs = np.where(m > 8)
    x0, x1 = int(xs.min()), int(xs.max()) + 1
    y0, y1 = int(ys.min()), int(ys.max()) + 1
    # CROPPED to its own bounds, not full-canvas. A full-canvas layer carrying
    # an OpacityMask is one element with both a mask and (when lit) a glow
    # Effect, and WPF is not dependable about honouring the mask in that
    # combination: if it drops, the layer floods the entire pad with the lit
    # colour. A layer bounded by its own control cannot do that.
    rgba = np.zeros((y1 - y0, x1 - x0, 4), np.uint8)
    rgba[:, :, :3] = 255
    rgba[:, :, 3] = m[y0:y1, x0:x1]
    p = os.path.join(OUTDIR, "mouse_%s.png" % name.lower())
    cv2.imwrite(p, rgba)
    return dict(name=name, file=os.path.basename(p),
                l=x0 / RW * VW, t=y0 / RH * VH,
                r=x1 / RW * VW, b=y1 / RH * VH)


def hit_path(members):
    """Clickable geometry. NEVER drawn, so a polygon approximation is fine
    here: WPF hit-tests a masked rectangle over its whole rect, not its mask,
    so each control needs real geometry to answer the mouse."""
    m = np.zeros((RH, RW), np.uint8)
    for r in members:
        m[lab == r["i"]] = 255
    m = cv2.dilate(m, np.ones((3, 3), np.uint8), iterations=2)
    cs, _ = cv2.findContours(m, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    c = max(cs, key=cv2.contourArea)
    pts = cv2.approxPolyDP(c, 0.0006 * cv2.arcLength(c, True), True).reshape(-1, 2)
    sx, sy = VW / RW, VH / RH
    return "M " + " L ".join("%.2f,%.2f" % (x * sx, y * sy) for x, y in pts) + " Z", len(pts)


bounds = {}
hits = {}
for name, members in layers.items():
    b = write_mask(name, members)
    bounds[name] = b
    if name != "Body":
        hits[name] = hit_path(members)
    print("  %-10s -> %-22s x %.1f..%.1f  y %.1f..%.1f"
          % (name, b["file"], b["l"], b["r"], b["t"], b["b"]))

wheel = bounds.get("Wheel")
body = bounds.get("Body")
cx = (wheel["l"] + wheel["r"]) / 2 if wheel else VW / 2

lines = [
    "// AUTO-GENERATED by tools/gen_mouse_art.py -- do not edit manually.",
    "//",
    "// Layers rendered from Zergatul.Obs.InputOverlay's mouse.svg.",
    "// Copyright (c) 2021 Igor Budzhak. Licensed under the MIT License.",
    "// https://github.com/Zergatul/Zergatul.Obs.InputOverlay",
    "//",
    "// mouse_line.png is the artwork as authored; every other layer is a",
    "// full-canvas alpha mask over it, so the view tints a control without",
    "// redrawing any of the shape. Nothing here approximates a curve.",
    "using System.Windows;",
    "",
    "namespace PadForge.Views;",
    "",
    "internal static class MouseArt",
    "{",
    "    internal const double W = %.4f;" % VW,
    "    internal const double H = %.4f;" % VH,
    "",
    '    internal const string Dir = "2DModels/MOUSE/";',
    '    internal const string Line = "mouse_line.png";',
    "",
]
for name, b in bounds.items():
    lines.append('    internal const string %s = "%s";' % (name, b["file"]))
    lines.append("    internal static readonly Rect %sRect = new(%.3f, %.3f, %.3f, %.3f);"
                 % (name, b["l"], b["t"], b["r"] - b["l"], b["b"] - b["t"]))
lines.append("")
lines.append("    // Clickable geometry. Never drawn: WPF hit-tests a masked")
lines.append("    // rectangle over its whole rect, not its mask, so each control")
lines.append("    // needs real geometry to answer the mouse.")
for name, (d, npts) in hits.items():
    lines.append("    /// <summary>%d-point hit region.</summary>" % npts)
    lines.append('    internal const string %sHit = "%s";' % (name, d))
lines.append("")
lines.append("    /// <summary>The art's own axis of symmetry, measured off the wheel.</summary>")
lines.append("    internal const double CenterX = %.3f;" % cx)
if wheel:
    lines.append("    internal const double WheelTop = %.3f;" % wheel["t"])
    lines.append("    internal const double WheelBottom = %.3f;" % wheel["b"])
if body:
    lines.append("    internal const double BodyTop = %.3f;" % body["t"])
    lines.append("    internal const double BodyBottom = %.3f;" % body["b"])
lines.append("}")
open(CS, "w", encoding="utf-8").write("\n".join(lines) + "\n")
print("emitted %s: %d mask layers + line-work" % (CS, len(bounds)))
