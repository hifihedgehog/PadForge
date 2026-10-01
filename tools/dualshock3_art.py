"""Draw the DualShock 3's 2D art and lay it out for the preview.

The pack the other controllers' art comes from (AL2009man's
Gamepad-Asset-Pack) has no DualShock 3, only a connection-icon pictogram.
So this draws one in the DualShock 4 set's own flat style: its body black,
button gray, outline gray, legend gray and symbol colors, its cyan press
ring and wash, and its PS logo, all taken from 2DModels/DS4. Shapes and
positions come from a front photograph of the controller (GameStop
product 10069638), measured once:

  * the outline is the photo's silhouette, mirrored about its center line
    (the shot is 99% symmetric) and traced to a 130-point half polygon,
  * the two raised discs, the stick bezels and caps, the face buttons and
    the PS button are circles fitted with a Hough transform,
  * the d-pad keys, SELECT and START are the photo's light-gray
    components, and the two cross-shaped recesses its darkest ones.

The photo is not in the repository. The traced polygon and the measured
primitives are, in the photo's pixels (1778 px across the body, 11.1 px
per mm), with x measured from the controller's center line and y from the
top of the photo.

The fronts of L2 and R2 sit behind L1 and R1 and do not show in the
photo. They are drawn the way the DualShock 4 art draws its triggers: a
cap of the bumper's width rising behind it, labeled, with the body in
front of its lower part.

tools/overlay_positions.py imports this and writes DualShock3Layout from
what build() returns. Rerunning rewrites every DS3_* file.
"""
import os

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

# ----------------------------------------------------------------------
#  Measured geometry, photo pixels
# ----------------------------------------------------------------------

# Left half of the silhouette, from the top of the center line around the
# left side to the bottom of the center line.
SILHOUETTE_LEFT = [
    (0.0, 302.5), (-47.0, 302.4), (-66.0, 299.7), (-177.0, 299.0), (-190.0, 300.0), (-294.0, 299.9),
    (-354.0, 299.1), (-357.7, 297.0), (-359.9, 292.0), (-364.1, 269.0), (-365.1, 259.0), (-367.4, 252.0),
    (-373.9, 219.0), (-380.4, 203.0), (-385.0, 195.8), (-394.9, 186.0), (-413.0, 173.0), (-425.0, 152.2),
    (-429.0, 147.8), (-441.0, 143.6), (-455.0, 140.2), (-497.0, 134.6), (-525.0, 134.6), (-555.0, 136.0),
    (-588.0, 140.6), (-614.0, 148.4), (-623.0, 154.0), (-626.0, 158.0), (-629.2, 168.0), (-633.0, 174.6),
    (-646.0, 185.0), (-656.6, 195.0), (-662.7, 204.0), (-665.6, 210.0), (-669.0, 221.0), (-677.3, 261.0),
    (-684.0, 300.0), (-688.5, 315.0), (-693.0, 321.2), (-719.0, 343.6), (-731.0, 356.0), (-739.0, 362.6),
    (-752.0, 379.6), (-755.9, 387.0), (-761.2, 394.0), (-782.4, 427.0), (-793.4, 454.0), (-800.8, 478.0),
    (-802.8, 489.0), (-808.8, 511.0), (-814.7, 537.0), (-825.7, 594.0), (-828.8, 617.0), (-834.8, 651.0),
    (-840.2, 687.0), (-848.3, 732.0), (-850.5, 750.0), (-853.1, 761.0), (-858.0, 797.0), (-867.5, 847.0),
    (-871.3, 876.0), (-880.5, 923.0), (-887.9, 975.0), (-889.5, 998.0), (-889.4, 1016.0), (-886.1, 1041.0),
    (-882.1, 1058.0), (-876.1, 1076.0), (-867.5, 1095.0), (-852.1, 1120.0), (-844.0, 1130.6), (-836.8, 1138.0),
    (-829.0, 1144.2), (-812.0, 1160.0), (-802.0, 1166.6), (-795.0, 1172.5), (-782.0, 1180.4), (-772.0, 1185.1),
    (-756.0, 1191.3), (-748.0, 1193.0), (-729.0, 1194.9), (-704.0, 1195.5), (-664.0, 1194.4), (-649.0, 1193.0),
    (-631.0, 1188.6), (-617.0, 1183.6), (-608.0, 1179.3), (-597.0, 1172.7), (-576.7, 1156.0), (-561.8, 1141.0),
    (-551.6, 1132.0), (-537.8, 1115.0), (-530.0, 1103.0), (-511.0, 1078.1), (-506.1, 1070.0), (-499.8, 1062.0),
    (-481.5, 1033.0), (-473.6, 1022.0), (-467.7, 1012.0), (-464.6, 1005.0), (-459.4, 998.0), (-444.8, 974.0),
    (-439.0, 967.0), (-424.0, 942.5), (-421.0, 939.6), (-417.0, 938.8), (-413.6, 940.0), (-405.0, 950.5),
    (-389.2, 965.0), (-375.0, 973.2), (-364.0, 981.3), (-342.0, 993.7), (-314.0, 1002.2), (-290.0, 1006.3),
    (-275.0, 1007.4), (-241.0, 1004.7), (-219.0, 1000.4), (-201.0, 994.7), (-188.0, 989.0), (-152.0, 968.4),
    (-145.8, 964.0), (-133.0, 953.3), (-112.3, 931.0), (-98.6, 913.0), (-91.5, 901.0), (-87.4, 891.0),
    (-84.0, 887.9), (-78.0, 885.8), (-70.0, 884.6), (0.0, 884.5),
]
SIL_TOP = 134.6

TOP_STRIP_Y = 350       # the face starts below the top edge's strip
TOWER_X = 356           # inner side of each shoulder tower
BUMPER_Y = 186          # bottom of the L1 / R1 cap at the tower's sides
BUMPER_SAG = 10         # and how much lower it reaches in the middle

DISC = (529, 566, 275)          # dx, y, r: the raised disc under each cluster
STICK = (267, 824)              # dx, y
BEZEL_R, WELL_R, CAP_R = 182, 137, 115

DPAD = (-530, 570)              # center of the d-pad cross
DPAD_ARM, DPAD_HALF = 160, (215, 210)
KEY_OFF = 95                    # key center from the cross center
KEY_ACROSS, KEY_ALONG = 87, 102
ARROW_OFF, ARROW_W, ARROW_H = 192, 24, 14

FACE = (528, 563)               # center of the face-button cross
FACE_ARM, FACE_HALF = 152, (228, 212)
FACE_OFF = (148, 137)
FACE_R = 57

PS = (0, 664, 50)
SELECT = (-158, 566, 78, 48)    # dx, y, w, h
START = (158, 566, 86, 54)
LABEL_Y = 503
SONY_Y = 415
LEDS = (-297, -137, 322)        # first and last LED x, and y
USB = (0, 322, 46, 16)

TRIGGER_RISE = 62               # L2 / R2 caps stand this far above L1 / R1
BAND = 26                       # back shell showing below each grip
BAND_FROM_Y = 760               # it starts below the discs
BAND_FROM_X = 430               # and outside the stick bulges

# ----------------------------------------------------------------------
#  Canvas
# ----------------------------------------------------------------------

SCALE = 0.81                    # photo to canvas: 1440 px across the body
CANVAS_W = 1466
TOP_MARGIN = 58                 # room for the L2 / R2 caps
SS = 4                          # every shape is drawn at 4x and reduced

BODY = (28, 25, 23, 255)
SHELL = (38, 30, 30, 255)
STRIP = (33, 30, 30, 255)
RECESS = (20, 18, 17, 255)
WELL = (14, 13, 12, 255)
LINE = (110, 107, 110, 255)
RIM = (33, 30, 30, 255)
CAP = (85, 84, 85, 255)
LEGEND = (189, 186, 186, 255)
LED = (61, 58, 58, 255)
TRIANGLE = (39, 200, 204, 255)
CIRCLE = (238, 118, 121, 255)
CROSS = (144, 180, 218, 255)
SQUARE = (203, 159, 200, 255)
PRESS_RING = (36, 210, 246, 255)
PRESS_WASH = (16, 120, 147, 128)

LINE_W = 2                      # canvas px
RIM_W = 3
RING_W = 6                      # press ring, as on DS4_Face_Button.png
STROKE_W = 6                    # face-button symbols

HERE = os.path.dirname(os.path.abspath(__file__))
DS4_DIR = os.path.join(os.path.dirname(HERE), "PadForge.App", "2DModels", "DS4")


def canvas_h():
    bottom = max(y for _, y in SILHOUETTE_LEFT)
    return int(round(TOP_MARGIN + (bottom - SIL_TOP) * SCALE + 8))


def px(dx, y):
    """Photo point (x from center line, y) to supersampled canvas pixels."""
    return ((CANVAS_W / 2 + dx * SCALE) * SS, (TOP_MARGIN + (y - SIL_TOP) * SCALE) * SS)


def plen(v):
    """Photo length to supersampled canvas pixels."""
    return v * SCALE * SS


# ----------------------------------------------------------------------
#  Masks and drawing
# ----------------------------------------------------------------------

def new_mask(size):
    return Image.new("L", size, 0)


def silhouette_points():
    left = [px(x, y) for x, y in SILHOUETTE_LEFT]
    right = [px(-x, y) for x, y in reversed(SILHOUETTE_LEFT)]
    return left + right


def circle_box(cx, cy, r):
    return (cx - r, cy - r, cx + r, cy + r)


def ring(draw, cx, cy, r, color, width):
    draw.ellipse(circle_box(cx, cy, r), outline=color, width=int(round(width * SS)))


def cross_points(c, arm, half):
    """A plus sign: arms ARM wide, reaching HALF from its center."""
    cx, cy = c
    a = arm / 2
    hx, hy = half
    pts = [(-a, -hy), (a, -hy), (a, -a), (hx, -a), (hx, a), (a, a), (a, hy),
           (-a, hy), (-a, a), (-hx, a), (-hx, -a), (-a, -a)]
    return [px(cx + x, cy + y) for x, y in pts]


def key_points(c, direction):
    """A d-pad key: square at its outer end, pointed toward the center."""
    cx, cy = c
    w, l = KEY_ACROSS / 2, KEY_ALONG / 2
    shape = [(-w, -l), (w, -l), (w, l * 0.35), (0, l), (-w, l * 0.35)]   # pointing down
    rot = {"Up": (1, 0, 0, 1), "Down": (-1, 0, 0, -1),
           "Left": (0, -1, 1, 0), "Right": (0, 1, -1, 0)}[direction]
    a, b, c2, d = rot
    return [px(cx + a * x + b * y, cy + c2 * x + d * y) for x, y in shape]


def disc_kernel(r):
    r = max(1, int(round(r)))
    return cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2 * r + 1, 2 * r + 1))


def local(mask, pad):
    """The mask's bounding box grown by PAD, clamped, and that crop."""
    bb = mask.getbbox()
    x0, y0 = max(0, bb[0] - pad), max(0, bb[1] - pad)
    x1, y1 = min(mask.width, bb[2] + pad), min(mask.height, bb[3] + pad)
    return (x0, y0, x1, y1), np.asarray(mask.crop((x0, y0, x1, y1)))


def round_mask(mask, radius):
    """Round a mask's corners, convex and concave, by RADIUS."""
    if radius <= 0 or mask.getbbox() is None:
        return mask
    box, a = local(mask, int(radius) * 2 + 2)
    k = disc_kernel(radius)
    a = cv2.morphologyEx(cv2.morphologyEx(a, cv2.MORPH_OPEN, k), cv2.MORPH_CLOSE, k)
    out = new_mask(mask.size)
    out.paste(Image.fromarray(a), box[:2])
    return out


def fill(img, mask, color):
    img.paste(color, (0, 0), mask)


def outline(img, mask, color, width):
    """A line WIDTH canvas px wide along the inside of a mask's edge."""
    if mask.getbbox() is None:
        return
    box, a = local(mask, 2)
    inner = cv2.erode(a, disc_kernel(width * SS))
    edge = np.where((a > 127) & (inner <= 127), 255, 0).astype(np.uint8)
    full = new_mask(mask.size)
    full.paste(Image.fromarray(edge), box[:2])
    fill(img, full, color)


def font(name, size):
    path = os.path.join(r"C:\Windows\Fonts", name)
    if not os.path.exists(path):
        raise SystemExit(f"font {name} not found")
    return ImageFont.truetype(path, int(size))


def text_centered(draw, cx, cy, text, fnt, color, tracking=0.0):
    """Text centered on its ink box, with optional letter spacing in em."""
    if tracking == 0.0:
        bb = draw.textbbox((0, 0), text, font=fnt)
        draw.text((cx - (bb[0] + bb[2]) / 2, cy - (bb[1] + bb[3]) / 2), text, font=fnt, fill=color)
        return
    gap = tracking * fnt.size
    widths = [draw.textlength(ch, font=fnt) for ch in text]
    total = sum(widths) + gap * (len(text) - 1)
    bb = draw.textbbox((0, 0), text, font=fnt)
    x = cx - total / 2
    y = cy - (bb[1] + bb[3]) / 2
    for ch, w in zip(text, widths):
        draw.text((x, y), ch, font=fnt, fill=color)
        x += w + gap


def reduce(img):
    """Supersampled RGBA to final size, filtering in premultiplied alpha."""
    w, h = img.size
    return img.convert("RGBa").resize((w // SS, h // SS), Image.LANCZOS).convert("RGBA")


def press_art(mask, rect=None):
    """The DS4 set's press art over one control: a cyan ring at full
    alpha around the edge and a translucent wash inside. Returns the
    final-size image and its canvas rect (x, y, w, h). RECT, in
    supersampled pixels, fixes the frame instead of fitting it."""
    if rect is None:
        x0, y0, x1, y1 = mask.getbbox()
        pad = (RING_W + 2) * SS
        x0, y0 = max(0, x0 - pad) // SS * SS, max(0, y0 - pad) // SS * SS
        x1, y1 = -(-(x1 + pad) // SS) * SS, -(-(y1 + pad) // SS) * SS
    else:
        x0, y0, x1, y1 = rect
    a = np.asarray(mask.crop((x0, y0, x1, y1)))
    inner = cv2.erode(a, disc_kernel(RING_W * SS))
    art = np.zeros((*a.shape, 4), np.uint8)
    art[a > 127] = PRESS_RING
    art[inner > 127] = PRESS_WASH
    out = reduce(Image.fromarray(art, "RGBA"))
    return out, (x0 // SS, y0 // SS, out.width, out.height), (x0, y0, x1, y1)


# ----------------------------------------------------------------------
#  The PS logo, from the DualShock 4 art
# ----------------------------------------------------------------------

def ps_logo(width_px):
    """The white PS logo cut out of DS4_V2_base.png at the given width."""
    base = Image.open(os.path.join(DS4_DIR, "DS4_V2_base.png")).convert("RGBA")
    a = np.asarray(base).astype(np.int32)
    crop = a[525:580, 695:772]
    lum = crop[:, :, :3].min(axis=2)
    alpha = np.clip((lum - 120) * 255 // 100, 0, 255) * (crop[:, :, 3] > 0)
    ys, xs = np.nonzero(alpha > 0)
    alpha = alpha[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
    logo = np.zeros((*alpha.shape, 4), np.uint8)
    logo[:, :, :3] = 255
    logo[:, :, 3] = alpha
    im = Image.fromarray(logo, "RGBA")
    h = int(round(im.height * width_px / im.width))
    return im.convert("RGBa").resize((width_px, h), Image.LANCZOS).convert("RGBA")


# ----------------------------------------------------------------------
#  Build
# ----------------------------------------------------------------------

def build(out_dir):
    os.makedirs(out_dir, exist_ok=True)
    W, H = CANVAS_W * SS, canvas_h() * SS
    size = (W, H)
    img = Image.new("RGBA", size, (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    lw = int(round(LINE_W * SS))
    rows = np.arange(H)[:, None]
    cols = np.arange(W)[None, :]
    results = []

    def save(name, im):
        im.save(os.path.join(out_dir, name))

    def record(name, target, etype, rect):
        x, y, w, h = rect
        results.append((name, target, etype, x, y, w, h))
        print(f"  {target:18s} {name:26s} -> ({x:4d}, {y:4d}) {w:4d}x{h:3d}")

    def emit(name, target, etype, mask):
        art, rect, _ = press_art(mask)
        save(name, art)
        record(name, target, etype, rect)

    # Body.
    body = new_mask(size)
    ImageDraw.Draw(body).polygon(silhouette_points(), fill=255)
    fill(img, body, BODY)
    body_a = np.asarray(body) > 127

    # Back shell along the bottom of each grip, the DualShock 4 set's
    # two-tone treatment: what the body covers minus the body moved up.
    shift = int(round(plen(BAND)))
    above = np.zeros_like(body_a)
    above[:-shift] = body_a[shift:]
    band = body_a & ~above & (rows > px(0, BAND_FROM_Y)[1]) \
        & (np.abs(cols - W / 2) > plen(BAND_FROM_X))
    fill(img, Image.fromarray(band.astype(np.uint8) * 255), SHELL)
    seam = band & np.roll(above, lw, axis=0)
    fill(img, Image.fromarray(seam.astype(np.uint8) * 255), LINE)
    del above, band, seam

    # Top edge between the shoulder towers.
    strip = new_mask(size)
    ImageDraw.Draw(strip).rectangle(
        (px(-TOWER_X, 0)[0], 0, px(TOWER_X, 0)[0], px(0, TOP_STRIP_Y)[1]), fill=255)
    strip = Image.fromarray(((np.asarray(strip) > 127) & body_a).astype(np.uint8) * 255)
    fill(img, strip, STRIP)
    d.line((px(-TOWER_X + 4, TOP_STRIP_Y), px(TOWER_X - 4, TOP_STRIP_Y)), fill=LINE, width=lw)
    x_first, x_last, y_led = LEDS
    for i in range(4):
        cx = x_first + (x_last - x_first) * i / 3
        d.rounded_rectangle((*px(cx - 8, y_led - 3), *px(cx + 8, y_led + 3)),
                            radius=plen(3), fill=LED)
    ux, uy, uw, uh = USB
    d.rounded_rectangle((*px(ux - uw / 2, uy - uh / 2), *px(ux + uw / 2, uy + uh / 2)),
                        radius=plen(4), outline=LINE, width=lw)

    # L1 and R1: the cap on top of each tower.
    bumpers = {}
    for side, sign in (("Left", -1), ("Right", 1)):
        # The cap's front edge bows down toward its middle, as in the photo.
        m = new_mask(size)
        edge = [px(sign * x, BUMPER_Y + BUMPER_SAG * max(0.0, 1 - ((x - 520) / 150) ** 2))
                for x in range(TOWER_X - 20, 921, 10)]
        ImageDraw.Draw(m).polygon([px(sign * (TOWER_X - 20), 0)] + edge + [px(sign * 920, 0)],
                                  fill=255)
        m = Image.fromarray(((np.asarray(m) > 127) & body_a).astype(np.uint8) * 255)
        fill(img, m, CAP)
        outline(img, m, RIM, RIM_W)
        bumpers[side] = m
        text_centered(d, *px(sign * 520, 166), "L1" if sign < 0 else "R1",
                      font("arial.ttf", 30 * SS), RIM)

    # Raised discs, drawn only where the body is.
    discs = new_mask(size)
    dd = ImageDraw.Draw(discs)
    for sign in (-1, 1):
        ring(dd, *px(sign * DISC[0], DISC[1]), plen(DISC[2]), 255, LINE_W)
    discs = Image.fromarray(((np.asarray(discs) > 127) & body_a).astype(np.uint8) * 255)
    fill(img, discs, LINE)

    # Stick bezels: a raised ring in body color over the discs, and the
    # hole inside it in near black.
    for sign in (-1, 1):
        cx, cy = px(sign * STICK[0], STICK[1])
        d.ellipse(circle_box(cx, cy, plen(BEZEL_R)), fill=BODY)
        ring(d, cx, cy, plen(BEZEL_R), LINE, LINE_W)
        d.ellipse(circle_box(cx, cy, plen(WELL_R)), fill=WELL)
        ring(d, cx, cy, plen(WELL_R), LINE, LINE_W)

    # The faceplate's lower edge between the bezels.
    y_edge = 843
    gap = np.sqrt(BEZEL_R ** 2 - (y_edge - STICK[1]) ** 2)
    d.line((px(-STICK[0] + gap, y_edge), px(STICK[0] - gap, y_edge)), fill=LINE, width=lw)

    # Cross-shaped recesses.
    for c, arm, half in ((DPAD, DPAD_ARM, DPAD_HALF), (FACE, FACE_ARM, FACE_HALF)):
        m = new_mask(size)
        ImageDraw.Draw(m).polygon(cross_points(c, arm, half), fill=255)
        m = round_mask(m, plen(10))
        fill(img, m, RECESS)
        outline(img, m, LINE, LINE_W)

    # D-pad direction marks at the ends of the cross.
    for dx, dy in ((0, -1), (0, 1), (-1, 0), (1, 0)):
        tip = (DPAD[0] + dx * (ARROW_OFF + ARROW_H / 2), DPAD[1] + dy * (ARROW_OFF + ARROW_H / 2))
        foot = (DPAD[0] + dx * (ARROW_OFF - ARROW_H / 2), DPAD[1] + dy * (ARROW_OFF - ARROW_H / 2))
        nx, ny = -dy, dx
        d.polygon([px(*tip),
                   px(foot[0] + nx * ARROW_W / 2, foot[1] + ny * ARROW_W / 2),
                   px(foot[0] - nx * ARROW_W / 2, foot[1] - ny * ARROW_W / 2)], fill=CAP)

    # D-pad keys.
    keys = {}
    for name, (dx, dy) in (("Up", (0, -1)), ("Down", (0, 1)), ("Left", (-1, 0)), ("Right", (1, 0))):
        m = new_mask(size)
        ImageDraw.Draw(m).polygon(
            key_points((DPAD[0] + dx * KEY_OFF, DPAD[1] + dy * KEY_OFF), name), fill=255)
        m = round_mask(m, plen(9))
        fill(img, m, CAP)
        outline(img, m, RIM, RIM_W)
        keys[name] = m

    # Face buttons and their symbols.
    face = {}
    fr = plen(FACE_R)
    sw = int(round(STROKE_W * SS))
    for target, (dx, dy), color, glyph in (
            ("ButtonA", (0, 1), CROSS, "cross"),
            ("ButtonB", (1, 0), CIRCLE, "circle"),
            ("ButtonX", (-1, 0), SQUARE, "square"),
            ("ButtonY", (0, -1), TRIANGLE, "triangle")):
        cx, cy = px(FACE[0] + dx * FACE_OFF[0], FACE[1] + dy * FACE_OFF[1])
        m = new_mask(size)
        ImageDraw.Draw(m).ellipse(circle_box(cx, cy, fr), fill=255)
        fill(img, m, CAP)
        outline(img, m, RIM, RIM_W)
        face[target] = m
        g = fr * 0.52
        if glyph == "cross":
            d.line((cx - g, cy - g, cx + g, cy + g), fill=color, width=sw)
            d.line((cx - g, cy + g, cx + g, cy - g), fill=color, width=sw)
        elif glyph == "circle":
            ring(d, cx, cy, g * 1.05, color, STROKE_W)
        elif glyph == "square":
            d.rectangle((cx - g * 0.9, cy - g * 0.9, cx + g * 0.9, cy + g * 0.9),
                        outline=color, width=sw)
        else:
            t = g * 1.1
            pts = [(cx, cy - t), (cx + t * 0.94, cy + t * 0.68), (cx - t * 0.94, cy + t * 0.68)]
            d.line(pts + [pts[0], pts[1]], fill=color, width=sw, joint="curve")

    # SELECT, START and their legends.
    small = {}
    x, y, w, h = SELECT
    m = new_mask(size)
    ImageDraw.Draw(m).rounded_rectangle((*px(x - w / 2, y - h / 2), *px(x + w / 2, y + h / 2)),
                                        radius=plen(10), fill=255)
    fill(img, m, CAP)
    outline(img, m, RIM, RIM_W)
    small["ButtonBack"] = m
    x, y, w, h = START
    m = new_mask(size)
    ImageDraw.Draw(m).polygon([px(x - w / 2, y - h / 2), px(x + w / 2, y), px(x - w / 2, y + h / 2)],
                              fill=255)
    m = round_mask(m, plen(6))
    fill(img, m, CAP)
    outline(img, m, RIM, RIM_W)
    small["ButtonStart"] = m
    label = font("arial.ttf", 24 * SS)
    text_centered(d, *px(SELECT[0], LABEL_Y), "SELECT", label, LEGEND)
    text_centered(d, *px(START[0], LABEL_Y), "START", label, LEGEND)
    text_centered(d, *px(0, SONY_Y), "SONY", font("timesbd.ttf", 56 * SS), LEGEND, tracking=0.12)

    # PS button: rim, chrome ring, and the DualShock 4 set's logo.
    cx, cy = px(PS[0], PS[1])
    pr = plen(PS[2])
    m = new_mask(size)
    ImageDraw.Draw(m).ellipse(circle_box(cx, cy, pr), fill=255)
    fill(img, m, RIM)
    ring(d, cx, cy, pr * 0.9, LINE, LINE_W)
    small["ButtonGuide"] = m

    base = reduce(img)
    logo = ps_logo(int(round(PS[2] * SCALE * 1.1)))
    base.alpha_composite(logo, (int(round(cx / SS - logo.width / 2)),
                                int(round(cy / SS - logo.height / 2))))
    save("DS3_base.png", base)
    print(f"  base {base.width}x{base.height}")

    # ---- Sprites ----------------------------------------------------

    # L2 / R2: a cap the bumper's width rising behind it. The rest art is
    # drawn behind the base, which hides its lower part. The press art
    # covers only what shows above the body. overlay_positions pairs the
    # two by one rect, so the rest art is cut to the press art's frame.
    rise = int(round(plen(TRIGGER_RISE)))
    for side, sign in (("Left", -1), ("Right", 1)):
        letter = "L" if sign < 0 else "R"
        bump = np.asarray(bumpers[side]) > 127
        # The bumper swept up by the rise: every copy of it from where it
        # sits to RISE above. The cap's top and sides follow the bumper's,
        # and it reaches down behind it, so no gap shows between the two.
        box, a = local(bumpers[side], rise + 2)
        swept = cv2.dilate(a, np.ones((rise + 1, 1), np.uint8), anchor=(0, 0))
        cap_l = new_mask(size)
        cap_l.paste(Image.fromarray(swept), box[:2])
        cap = np.asarray(cap_l) > 127
        cap_img = round_mask(Image.fromarray(cap.astype(np.uint8) * 255), plen(6))
        shown = Image.fromarray(((np.asarray(cap_img) > 127) & ~body_a).astype(np.uint8) * 255)
        art, rect, frame = press_art(shown)
        name = f"DS3_{letter}2-Active.png"
        save(name, art)
        record(name, f"{side}Trigger", "Trigger", rect)
        x0, y0, x1, y1 = frame
        rest = Image.new("RGBA", (x1 - x0, y1 - y0), (0, 0, 0, 0))
        cm = cap_img.crop(frame)
        fill(rest, cm, CAP)
        outline(rest, cm, RIM, RIM_W)
        top = np.nonzero(np.asarray(shown.crop(frame)).any(axis=1))[0]
        bump_top = np.nonzero(bump.any(axis=1))[0].min() - y0
        text_centered(ImageDraw.Draw(rest), (x1 - x0) / 2, (top.min() + bump_top) / 2,
                      f"{letter}2", font("arial.ttf", 28 * SS), RIM)
        save(f"DS3_{letter}2.png", reduce(rest))

    emit("DS3_Face_Button.png", "ButtonA", "Button", face["ButtonA"])
    for t in ("ButtonB", "ButtonX", "ButtonY"):
        _, rect, _ = press_art(face[t])
        record("DS3_Face_Button.png", t, "Button", rect)
    for name in ("Up", "Down", "Left", "Right"):
        emit(f"DS3_D-PAD_{name}.png", f"DPad{name}", "Button", keys[name])
    emit("DS3_L1-Active.png", "LeftShoulder", "Button", bumpers["Left"])
    emit("DS3_R1-Active.png", "RightShoulder", "Button", bumpers["Right"])
    emit("DS3_Select_Button.png", "ButtonBack", "Button", small["ButtonBack"])
    emit("DS3_Start_Button.png", "ButtonStart", "Button", small["ButtonStart"])
    emit("DS3_Home_Button.png", "ButtonGuide", "Button", small["ButtonGuide"])

    # Stick caps: gray, rimmed, with the line where the domed top meets
    # the side. The click art is the press art over the same disc.
    r = plen(CAP_R)
    edge = int(np.ceil((r + RIM_W * SS) / SS)) * SS + SS
    cm = new_mask((edge * 2, edge * 2))
    ImageDraw.Draw(cm).ellipse(circle_box(edge, edge, r), fill=255)
    cap = Image.new("RGBA", cm.size, (0, 0, 0, 0))
    fill(cap, cm, CAP)
    outline(cap, cm, RIM, RIM_W)
    ring(ImageDraw.Draw(cap), edge, edge, r * 0.74, LED, LINE_W)
    cap_img = reduce(cap)
    save("DS3_AnalogStick.png", cap_img)
    click_img, _, _ = press_art(cm, (0, 0, cm.width, cm.height))
    save("DS3_AnalogStick_Click.png", click_img)
    for kind, target, image in (("StickRing", "ThumbRing", cap_img),
                                ("StickClick", "ThumbButton", click_img)):
        for side, sign in (("Left", -1), ("Right", 1)):
            cx, cy = px(sign * STICK[0], STICK[1])
            rect = (int(round(cx / SS - image.width / 2)), int(round(cy / SS - image.height / 2)),
                    image.width, image.height)
            record(f"DS3_AnalogStick{'_Click' if kind == 'StickClick' else ''}.png",
                   f"{side}{target}", kind, rect)

    return {"base_width": base.width, "base_height": base.height, "results": results}


if __name__ == "__main__":
    out = os.path.join(os.path.dirname(HERE), "PadForge.App", "2DModels", "DS3")
    data = build(out)
    print(f"{len(data['results'])} elements on a {data['base_width']}x{data['base_height']} base")
