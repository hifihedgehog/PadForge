"""Convert the DualShock 3 model into PadForge's per-part OBJs and atlas.

Source: "Sony PlayStation Dualshock 3 wireless controller" by 3doverstock,
bought on CGTrader under its Royalty Free License
(cgtrader.com/3d-models/electronics/computer/sony-playstation-dualshock-3-wireless-controller).
Its OBJ archive holds one Wavefront mesh in centimeters with a normal on
every corner, and a 4096 px diffuse that already carries every printed
mark: the SONY logo, SELECT and START, the four colored face-button
symbols, the d-pad arrows, the L and R lettering, the panel seams and the
player-LED numbers. One atlas skins every part, so there is no decal set.

Usage:
    pip install numpy pillow
    set DS3_ZIP=<path to Sony__PlayStation_Dualshock_3_wireless_controller_obj.zip>
    python tools/dualshock3_mesh.py

Parts. The mesh is 16 loose pieces: the shell with both sticks welded
into it, L1, R1, L2, R2, the four face buttons, the PS button, the four
d-pad keys, SELECT and START. Each loose piece is named by where it sits,
and the run stops if any piece is missing or matched twice.

Sticks. Each stick is cut out of the shell. Its dome sits in a groove
whose outer wall, the collar, rises to the faceplate. The collar is the
wall of the faceplate hole, faces the stick, and stays with the body,
because a well bezel never moves. Without it each stick meets the shell
at exactly one ring of 26 vertices, the groove's bottom, and the run
stops if that ring is not what it finds. The cap's top down to the rim's
widest point is the ring, the direction surface, and the rim's underside,
the stem and the dome are the click. That is the anatomy of every other
stick in the tree: the widest point sits 3.7 mm behind the cap's apex on
a 9.7 mm cap, 0.38 cap radii against the 0.40 the others measure.

Pivot. The dome is a sphere to a tenth of a millimeter, and a dome is
shaped that way so it turns in place about the stick's pivot and keeps
filling the hole at every angle. Its center is therefore the pivot, and
the run prints it for the model class.

Axes. The source is X right, Y out of the face, Z toward the player.
PadForge wants X right, Y into the controller, Z toward the top edge, in
millimeters, so the map is (10x, -10y, -10z): a half turn about X, which
keeps the winding. The result is centered on its own bounding box, the
way the other bought models are.
"""
import io
import os
import sys
import zipfile

import numpy as np
from PIL import Image

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DST = os.path.join(PROJ_ROOT, "PadForge.App", "3DModels", "DS3")
ZIP = os.environ.get("DS3_ZIP", os.path.join(
    os.path.expanduser("~"), "Downloads", "DualShock 3",
    "Sony__PlayStation_Dualshock_3_wireless_controller_obj.zip"))
OBJ_NAME = "Sony__PlayStation_Dualshock_3_wireless_controller.obj"
DIFFUSE_NAME = "Sony4G_Diffuse.png"

CREDIT = ("Sony PlayStation DualShock 3 by 3doverstock, "
          "CGTrader Royalty Free License")

# Every loose piece but the shell, by its centroid in source centimeters.
# B1 to B4 follow the PlayStation positions the DS4 and DualSense use:
# cross at the bottom is B1, the button the raw surface calls ButtonA.
LOOSE = [
    ("Shoulder-Left-Trigger.obj",  (-4.60, 2.18, -4.21)),
    ("Shoulder-Right-Trigger.obj", ( 4.60, 2.18, -4.21)),
    ("L1.obj",                     (-4.60, 4.12, -4.64)),
    ("R1.obj",                     ( 4.60, 4.12, -4.64)),
    ("B1.obj",                     ( 4.62, 5.46,  0.01)),   # cross
    ("B2.obj",                     ( 5.93, 5.46, -1.21)),   # circle
    ("B3.obj",                     ( 3.32, 5.46, -1.19)),   # square
    ("B4.obj",                     ( 4.62, 5.46, -2.39)),   # triangle
    ("Special.obj",                (-0.01, 5.07, -0.27)),   # PS
    ("DPadUp.obj",                 (-4.66, 5.46, -2.03)),
    ("DPadDown.obj",               (-4.66, 5.46, -0.35)),
    ("DPadLeft.obj",               (-5.50, 5.46, -1.19)),
    ("DPadRight.obj",              (-3.83, 5.46, -1.19)),
    ("Back.obj",                   (-1.42, 5.11, -1.17)),   # SELECT
    ("Start.obj",                  ( 1.28, 5.10, -1.17)),
]
MATCH_TOL = 0.05      # cm

# Stick region in source centimeters. Faces whose centroid sits within
# STICK_R of a stick's axis and above the well floor (y 2.32) belong to
# the stick or to its collar. The collar's faces sit past COLLAR_R and
# face the axis. Every stick face faces away from it or up.
STICK_R = 1.25
WELL_FLOOR_Y = 2.40
COLLAR_R = 1.05
CUT_RING_VERTS = 26

ATLAS_PX = 2048
PAD_ITER = 16

# The diffuse is a near-black controller with its lighting baked in, and
# the preview's rig draws a face-on texel at about two thirds of its value:
# the faceplate rendered at 13 of 255 where the reference photo (GameStop
# product 10069638) reads 30, and the stick caps at 27 against 56. A gamma
# lifts the dark plastic toward the photo and leaves the white print and
# the colored symbols nearly where they were.
TONE_GAMMA = 0.75


# ----------------------------------------------------------------------
#  Source in
# ----------------------------------------------------------------------

def read_source(path):
    """Positions, texcoords, normals, and faces as lists of (v, vt, vn)."""
    with zipfile.ZipFile(path) as z:
        text = z.read(OBJ_NAME).decode("utf-8", errors="replace")
        diffuse = Image.open(io.BytesIO(z.read(DIFFUSE_NAME))).convert("RGB")
    v, vt, vn, faces = [], [], [], []
    for line in text.splitlines():
        if line.startswith("v "):
            v.append([float(x) for x in line.split()[1:4]])
        elif line.startswith("vt "):
            vt.append([float(x) for x in line.split()[1:3]])
        elif line.startswith("vn "):
            vn.append([float(x) for x in line.split()[1:4]])
        elif line.startswith("f "):
            corners = []
            for tok in line.split()[1:]:
                a, b, c = (tok.split("/") + ["", ""])[:3]
                corners.append((int(a) - 1, int(b) - 1, int(c) - 1))
            faces.append(corners)
    return np.array(v), np.array(vt), np.array(vn), faces, diffuse


def components(vcount, faces):
    parent = np.arange(vcount)

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    for f in faces:
        r = find(f[0][0])
        for c in f[1:]:
            s = find(c[0])
            if s != r:
                parent[s] = r
    roots = np.array([find(f[0][0]) for f in faces])
    groups = {}
    for i, r in enumerate(roots):
        groups.setdefault(int(r), []).append(i)
    return sorted(groups.values(), key=len, reverse=True)


def centroid(v, faces, idx):
    return v[[c[0] for i in idx for c in faces[i]]].mean(axis=0)


# ----------------------------------------------------------------------
#  Naming
# ----------------------------------------------------------------------

def name_loose(v, faces, comps):
    """The shell is the largest piece. Every other piece is matched to
    LOOSE by centroid, one to one."""
    parts = {"MainBody.obj": list(comps[0])}
    taken = set()
    for idx in comps[1:]:
        c = centroid(v, faces, idx)
        hits = [name for name, ref in LOOSE
                if np.linalg.norm(c - np.array(ref)) < MATCH_TOL]
        if len(hits) != 1 or hits[0] in taken:
            raise SystemExit(f"loose piece at {np.round(c, 3)} matched {hits}")
        taken.add(hits[0])
        parts[hits[0]] = list(idx)
    missing = [name for name, _ in LOOSE if name not in taken]
    if missing:
        raise SystemExit(f"no piece found for {missing}")
    return parts


def stick_axes(v, shell, faces):
    """The two cap apexes: the highest points of the shell piece, one on
    each side of the center line."""
    pts = v[sorted({c[0] for i in shell for c in faces[i]})]
    top = pts[pts[:, 1] > pts[:, 1].max() - 0.01]
    left, right = top[top[:, 0] < 0], top[top[:, 0] > 0]
    if not len(left) or not len(right):
        raise SystemExit("could not find both stick caps")
    return [(left[:, 0].mean(), left[:, 2].mean()),
            (right[:, 0].mean(), right[:, 2].mean())]


def face_normal(p):
    n = np.cross(p[1] - p[0], p[2] - p[0])
    return n / max(np.linalg.norm(n), 1e-12)


def cut_stick(v, faces, shell, axis):
    """Split one stick off the shell. Returns (ring faces, click faces,
    pivot height in source cm), and leaves the collar in the shell."""
    ax, az = axis
    stick = []
    for i in shell:
        p = v[[c[0] for c in faces[i]]]
        c = p.mean(axis=0)
        r = np.hypot(c[0] - ax, c[2] - az)
        if r >= STICK_R or c[1] <= WELL_FLOOR_Y:
            continue
        out = np.array([c[0] - ax, 0.0, c[2] - az])
        out /= max(np.linalg.norm(out), 1e-12)
        if r > COLLAR_R and np.dot(face_normal(p), out) < 0:
            continue                      # collar: faces the stick, body
        stick.append(i)

    sset = set(stick)
    sv = {c[0] for i in stick for c in faces[i]}
    rest = {c[0] for i in shell if i not in sset for c in faces[i]}
    seam = sorted(sv & rest)
    sp = v[seam]
    sr = np.hypot(sp[:, 0] - ax, sp[:, 2] - az)
    if len(seam) != CUT_RING_VERTS or np.ptp(sp[:, 1]) > 1e-3 or np.ptp(sr) > 5e-3:
        raise SystemExit(
            f"stick at x={ax:.3f}: cut is {len(seam)} vertices, "
            f"y {sp[:, 1].min():.4f}..{sp[:, 1].max():.4f}, "
            f"r {sr.min():.4f}..{sr.max():.4f}; expected one ring of {CUT_RING_VERTS}")

    pts = v[sorted(sv)]
    r = np.hypot(pts[:, 0] - ax, pts[:, 2] - az)

    # The stick's profile, apex first: each height the mesh has a vertex
    # ring at, with that ring's radius. Going down it widens to the rim,
    # narrows to the stem, and widens again over the dome.
    keys = np.round(pts[:, 1], 3)
    levels = sorted(set(keys.tolist()), reverse=True)
    prof = np.array([r[keys == y].max() for y in levels])
    k = 0
    while k + 1 < len(prof) and prof[k + 1] >= prof[k]:
        k += 1
    widest_y = levels[k]
    stem_r = prof[k:].min()
    stem = [j for j in range(k, len(prof)) if prof[j] < stem_r + 0.01]
    stem_bottom = levels[max(stem)]
    print("    profile (y, r): " + ", ".join(
        f"({y:.3f}, {q:.3f})" for y, q in zip(levels, prof)))

    # The ring runs from the apex down to the rim's widest point.
    ring = [i for i in stick
            if v[[c[0] for c in faces[i]], 1].min() >= widest_y - 1e-3]
    rset = set(ring)
    click = [i for i in stick if i not in rset]

    # Sphere fit to the dome, every vertex below the stem.
    # r^2 + (y - c)^2 = R^2 is linear in c and R^2 - c^2.
    dome = keys < stem_bottom - 1e-3
    d, dr = pts[dome], r[dome]
    a = np.column_stack([2 * d[:, 1], np.ones(len(d))])
    sol = np.linalg.lstsq(a, dr ** 2 + d[:, 1] ** 2, rcond=None)[0]
    cy = sol[0]
    rad = np.sqrt(sol[1] + cy * cy)
    resid = np.abs(np.sqrt(dr ** 2 + (d[:, 1] - cy) ** 2) - rad).max()

    print(f"  stick x={ax:+.3f} z={az:.3f}: {len(ring)} ring + {len(click)} click faces, "
          f"cut ring {len(seam)} verts at y {sp[:, 1].mean():.4f} r {sr.mean():.4f}")
    print(f"    rim widest r {prof[k]:.4f} at y {widest_y:.4f}, apex y {levels[0]:.4f}, "
          f"stem r {stem_r:.4f} down to y {stem_bottom:.4f}")
    print(f"    dome sphere center y {cy:.4f} R {rad:.4f} "
          f"(worst residual {resid * 10:.3f} mm over {len(d)} verts)")
    if resid > 0.02:
        raise SystemExit("dome is not a sphere, so the pivot would be a guess")
    return ring, click, cy


# ----------------------------------------------------------------------
#  Output
# ----------------------------------------------------------------------

def to_padforge(p):
    return np.column_stack([10.0 * p[:, 0], -10.0 * p[:, 1], -10.0 * p[:, 2]])


def to_padforge_n(n):
    return np.column_stack([n[:, 0], -n[:, 1], -n[:, 2]])


def write_obj(path, v, vt, vn, faces, idx, center):
    used_v = sorted({c[0] for i in idx for c in faces[i]})
    used_t = sorted({c[1] for i in idx for c in faces[i]})
    used_n = sorted({c[2] for i in idx for c in faces[i]})
    mv = {k: j + 1 for j, k in enumerate(used_v)}
    mt = {k: j + 1 for j, k in enumerate(used_t)}
    mn = {k: j + 1 for j, k in enumerate(used_n)}
    pv = to_padforge(v[used_v]) - center
    pn = to_padforge_n(vn[used_n])
    stem = os.path.splitext(os.path.basename(path))[0]
    with io.open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(f"# {stem}\n# {CREDIT}\no {stem}\n")
        for p in pv:
            fh.write("v %.4f %.4f %.4f\n" % tuple(p))
        for t in vt[used_t]:
            fh.write("vt %.6f %.6f\n" % tuple(t))
        for n in pn:
            fh.write("vn %.4f %.4f %.4f\n" % tuple(n))
        for i in idx:
            fh.write("f " + " ".join(
                f"{mv[a]}/{mt[b]}/{mn[c]}" for a, b, c in faces[i]) + "\n")
    return len(idx), len(used_v)


def bake_atlas(diffuse, vt, faces, path):
    """The source diffuse at 2048 px and TONE_GAMMA, with every UV
    island's colors dilated into the texels around it. Bilinear filtering
    samples past an island's edge, and whatever sits there bleeds in as a
    bright speck at the seam unless it is the island's own color."""
    img = np.asarray(diffuse.resize((ATLAS_PX, ATLAS_PX), Image.LANCZOS),
                     dtype=np.float32)
    img = 255.0 * (np.clip(img, 0, 255) / 255.0) ** TONE_GAMMA
    mask = Image.new("L", (ATLAS_PX, ATLAS_PX), 0)
    from PIL import ImageDraw
    draw = ImageDraw.Draw(mask)
    for f in faces:
        uv = vt[[c[1] for c in f]]
        pts = [(u * ATLAS_PX, (1.0 - w) * ATLAS_PX) for u, w in uv]
        draw.polygon(pts, fill=255, outline=255)
    covered = np.asarray(mask) > 0
    print(f"  atlas: islands cover {covered.mean() * 100:.1f}% of {ATLAS_PX} px")

    h, w = covered.shape
    for _ in range(PAD_ITER):
        acc = np.zeros_like(img)
        cnt = np.zeros((h, w), np.float32)
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                if dx == 0 and dy == 0:
                    continue
                # Clamped shifts: a wrapped shift bleeds one atlas edge's
                # colors into the opposite edge's islands.
                ys = slice(max(dy, 0), h + min(dy, 0))
                yd = slice(max(-dy, 0), h + min(-dy, 0))
                xs = slice(max(dx, 0), w + min(dx, 0))
                xd = slice(max(-dx, 0), w + min(-dx, 0))
                m = covered[ys, xs]
                acc[yd, xd][m] += img[ys, xs][m]
                cnt[yd, xd][m] += 1
        grow = (~covered) & (cnt > 0)
        img[grow] = acc[grow] / cnt[grow][:, None]
        covered = covered | grow

    out = Image.fromarray(np.clip(img + 0.5, 0, 255).astype(np.uint8), "RGB")
    out.save(path, "JPEG", quality=92, subsampling=0, optimize=True)
    print(f"  Body.jpg {os.path.getsize(path):,} bytes")


def main():
    os.makedirs(DST, exist_ok=True)
    v, vt, vn, faces, diffuse = read_source(ZIP)
    print(f"  {len(v):,} positions, {len(faces):,} faces")

    comps = components(len(v), faces)
    print(f"  {len(comps)} loose pieces")
    parts = name_loose(v, faces, comps)

    shell = parts["MainBody.obj"]
    pivots = []
    for side, axis in zip(("Left", "Right"), stick_axes(v, shell, faces)):
        ring, click, cy = cut_stick(v, faces, shell, axis)
        taken = set(ring) | set(click)
        shell = [i for i in shell if i not in taken]
        parts[f"Joystick-{side}-Ring.obj"] = ring
        parts[f"{side}StickClick.obj"] = click
        pivots.append((side, axis, cy))
    parts["MainBody.obj"] = shell

    allp = to_padforge(v)
    center = (allp.min(axis=0) + allp.max(axis=0)) / 2.0
    lo, hi = allp.min(axis=0) - center, allp.max(axis=0) - center
    print(f"  centered on ({center[0]:.2f}, {center[1]:.2f}, {center[2]:.2f}) mm: "
          f"X[{lo[0]:.1f},{hi[0]:.1f}] Y[{lo[1]:.1f},{hi[1]:.1f}] Z[{lo[2]:.1f},{hi[2]:.1f}]")

    for name in sorted(parts):
        nf, nv = write_obj(os.path.join(DST, name), v, vt, vn, faces, parts[name], center)
        print(f"  {name:28s} {nf:5,} faces  {nv:5,} verts")

    body = to_padforge(v[sorted({c[0] for i in parts['MainBody.obj'] for c in faces[i]})])
    print(f"  MainBody width {np.ptp(body[:, 0]):.2f} mm")

    for side, (ax, az), cy in pivots:
        p = to_padforge(np.array([[ax, cy, az]]))[0] - center
        print(f"  {side} stick pivot ({p[0]:.3f}, {p[1]:.3f}, {p[2]:.3f}) mm")

    # Trigger hinge: a third of the way up the trigger in its own bounds,
    # the fraction the Xbox One model has always used (0.327 of the depth
    # span, 0.371 of the height span, from the front and the bottom).
    for name in ("Shoulder-Left-Trigger.obj", "Shoulder-Right-Trigger.obj"):
        t = to_padforge(v[sorted({c[0] for i in parts[name] for c in faces[i]})]) - center
        tlo, thi = t.min(axis=0), t.max(axis=0)
        hy = tlo[1] + 0.327 * (thi[1] - tlo[1])
        hz = tlo[2] + 0.371 * (thi[2] - tlo[2])
        print(f"  {name:28s} hinge ({(tlo[0] + thi[0]) / 2:.3f}, {hy:.3f}, {hz:.3f}) mm, "
              f"bounds Y[{tlo[1]:.2f},{thi[1]:.2f}] Z[{tlo[2]:.2f},{thi[2]:.2f}]")

    bake_atlas(diffuse, vt, faces, os.path.join(DST, "Body.jpg"))


if __name__ == "__main__":
    main()
