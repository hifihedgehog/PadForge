"""Cut Valve's single-solid 2026 mesh into PadForge's per-part OBJ set.

Runs after sc2026_segment.py, which saved the crease patches. Two sources,
each used for what it is authoritative about:

  geometry   the crease patches, which follow the real part boundaries
             because CAD output has exact sharp edges
  naming     the front elevation from Valve's reference drawing, whose
             control silhouettes the 2D asset is already built from

A patch is named by projecting its centroid onto the drawing and asking
which control silhouette it lands in. The projection is exact and was
verified before it was trusted: the ABXY cluster, both trackpads and the
Steam button each land within a pixel of where the generated 2D layout
places them.

Axes. The drawing fixes the orientation. X is width and matches directly.
Z is height measured DOWNWARD, which is why the D-pad at Z 20 lands at the
TOP of the sheet. Y is depth with the front at positive. PadForge wants X
width, Y depth with the front NEGATIVE, and Z height upward, so the map is
(x, -y, -z). Two sign flips is a rotation about X, not a mirror, so the
winding is left alone.
"""
import numpy as np
import os
import io
import sys
import struct
import cv2
import fast_simplification as fs
import scipy.ndimage as nd
from scipy import sparse
from scipy.sparse import csgraph


PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DST = os.path.join(PROJ_ROOT, "PadForge.App", "3DModels", "SteamController2")
GEN = os.path.join(PROJ_ROOT, "tools")

# Valve's own published CAD, cloned beside this repo the same way the
# asset pack is. Override with SC2026_STL to point elsewhere.
STL = os.environ.get("SC2026_STL",
                     os.path.join(os.path.dirname(PROJ_ROOT),
                                  "SteamControllerHardware",
                                  "sc_solid_stl_20260429.stl"))

# Front-elevation registration, read off the drawing and the mesh rather
# than assumed. The clip rect carried 6 pt of padding around a 900 x 627.9
# pt body box, rendered at 468 DPI and downsampled by 4.
PAD_PT, DPI, SS = 6.0, 468.0, 4
BODY_PT_W, BODY_PT_H = 900.0, 627.9
PX_PER_PT = DPI / 72.0 / SS
OFF = PAD_PT * PX_PER_PT
SPAN_X, SPAN_Y = BODY_PT_W * PX_PER_PT, BODY_PT_H * PX_PER_PT

# Triangle budget per part, sized against the sets already shipped here
# (Steam Deck 55k, Switch 2 Pro 55k, Xbox 360 49k).
BUDGET = {
    "MainBody.obj": 26000,
    "LeftPadTouch.obj": 3000, "RightPadTouch.obj": 3000,
    "LeftStickClick.obj": 2600, "RightStickClick.obj": 2600,
    "Joystick-Left-Ring.obj": 1800, "Joystick-Right-Ring.obj": 1800,
    "DPadUp.obj": 900, "DPadDown.obj": 900,
    "DPadLeft.obj": 900, "DPadRight.obj": 900,
    "B1.obj": 800, "B2.obj": 800, "B3.obj": 800, "B4.obj": 800,
    "Special.obj": 900, "Back.obj": 700, "Start.obj": 700,
    "ThreeDots.obj": 700,
    "L1.obj": 2200, "R1.obj": 2200,
    "Shoulder-Left-Trigger.obj": 2600, "Shoulder-Right-Trigger.obj": 2600,
    "L4.obj": 1400, "R4.obj": 1400, "L5.obj": 1400, "R5.obj": 1400,
}
DEFAULT_BUDGET = 1500


def cad_to_px(x, z):
    """Front-elevation pixel for a CAD (X, Z), in final asset pixels."""
    return (OFF + (x + 79.4) / 158.8 * SPAN_X,
            OFF + (z + 5.8) / 110.8 * SPAN_Y)


def control_masks():
    """The named control silhouettes, from the same code that builds the
    2D asset, so the two never drift apart."""
    sys.path.insert(0, GEN)
    import importlib.util
    spec = importlib.util.spec_from_file_location(
        "overlay_positions", os.path.join(GEN, "overlay_positions.py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)

    gray = mod._sc2_outline()
    lab, stats, cent, body, named, _ = mod._sc2_regions(gray)
    masks = {k: (lab == v) for k, v in named.items()}
    if "DPad" in named:
        masks.pop("DPad")
        masks.update(mod._sc2_dpad_quadrants(lab == named["DPad"]))
    # Downsample each mask to asset pixels so the lookup is cheap.
    out = {}
    for k, m in masks.items():
        small = cv2.resize(m.astype(np.uint8), (m.shape[1] // SS, m.shape[0] // SS),
                           interpolation=cv2.INTER_NEAREST).astype(bool)
        # Fill the mask's own holes. A face-button silhouette is the disc
        # MINUS its glyph, because the glyph is stroked as its own region,
        # and an unfilled disc rejects points at its exact centre. That is
        # where a button's geometry projects, so every letter button and
        # the Steam button matched nothing until this ran.
        small = nd.binary_fill_holes(small)
        # Grow a little: geometry can project just outside the drawn
        # silhouette where the outline stroke eats the edge.
        out[k] = nd.binary_dilation(small, iterations=3)
    return out


# Control name in the drawing -> PadForge part filename.
PART = {
    "LeftTouchpadClick": "LeftPadTouch.obj",
    "RightTouchpadClick": "RightPadTouch.obj",
    "LeftThumbButton": "LeftStickClick.obj",
    "RightThumbButton": "RightStickClick.obj",
    "LeftThumbRing": "Joystick-Left-Ring.obj",
    "RightThumbRing": "Joystick-Right-Ring.obj",
    "DPadUp": "DPadUp.obj", "DPadDown": "DPadDown.obj",
    "DPadLeft": "DPadLeft.obj", "DPadRight": "DPadRight.obj",
    "ButtonA": "B1.obj", "ButtonB": "B2.obj",
    "ButtonX": "B3.obj", "ButtonY": "B4.obj",
    "ButtonGuide": "Special.obj",
    "ButtonBack": "Back.obj", "ButtonStart": "Start.obj",
    "ButtonQuickAccess": "ThreeDots.obj",
}


# ----------------------------------------------------------------------
#  Crease segmentation
# ----------------------------------------------------------------------
#
# Segment Valve's single-solid 2026 Steam Controller mesh into parts.
# 
# Valve published the 2026 pad as ONE merged closed shell (verified: the STL
# welds to a single connected component, and the STEP carries one
# MANIFOLD_SOLID_BREP with one CLOSED_SHELL and 848 unnamed ADVANCED_FACEs
# under a single product, IBEX_SOLID). Unlike the 2015 release, which ships
# 22 named part files, there is no part decomposition to recover from the
# source, so the parts have to be found in the geometry.
# 
# The method is crease segmentation, which works here because this is CAD
# output rather than a sculpt: every real part boundary is a sharp edge, and
# every smooth surface is smooth to within tessellation noise.
# 
# 1. Weld vertices at CAD tolerance and build the triangle adjacency across
#    shared edges.
# 2. Cut adjacency wherever the dihedral angle exceeds the crease threshold.
# 3. Connected components of what remains are surface patches.
# 4. Name patches by projecting them onto the front elevation and testing
#    which control silhouette they land in. The silhouettes come from the
#    same reference drawing the 2D asset is built from, so the naming has
#    one authority rather than two.

# Crease threshold. CAD output is smooth to within tessellation noise on
# a real surface and sharp at every real part boundary, so the cut is not
# delicate: the dihedral angle across this mesh has a median of 0.24
# degrees and a 95th percentile of 3.75.
CREASE_DEG = 22.0

# Vertex weld tolerance, in millimetres.
WELD = 1e-3


def read_stl(path):
    with open(path, "rb") as f:
        head = f.read(84)
        n = struct.unpack("<I", head[80:84])[0]
        raw = np.frombuffer(f.read(n * 50), np.uint8)
    n = min(n, raw.size // 50)
    rec = raw[:n * 50].reshape(n, 50)
    return np.frombuffer(rec[:, 12:48].tobytes(), "<f4").reshape(n, 3, 3).astype(np.float64)


def weld(tris):
    q = np.round(tris.reshape(-1, 3) / WELD).astype(np.int64)
    uniq, inv = np.unique(q, axis=0, return_inverse=True)
    return uniq.astype(np.float64) * WELD, np.asarray(inv).ravel().reshape(-1, 3)


def normals(verts, faces):
    a, b, c = verts[faces[:, 0]], verts[faces[:, 1]], verts[faces[:, 2]]
    nrm = np.cross(b - a, c - a)
    ln = np.linalg.norm(nrm, axis=1, keepdims=True)
    return nrm / np.maximum(ln, 1e-12)


def segment(verts, faces, crease_deg):
    n = len(faces)
    # Every triangle contributes three undirected edges. An edge shared by
    # exactly two triangles is an adjacency candidate.
    e = np.concatenate([faces[:, [0, 1]], faces[:, [1, 2]], faces[:, [2, 0]]])
    e.sort(axis=1)
    owner = np.tile(np.arange(n), 3)
    key = e[:, 0].astype(np.int64) * (faces.max() + 1) + e[:, 1]
    order = np.argsort(key, kind="stable")
    key, owner = key[order], owner[order]
    same = key[1:] == key[:-1]
    i = np.nonzero(same)[0]
    fa, fb = owner[i], owner[i + 1]

    nrm = normals(verts, faces)
    dot = np.clip(np.einsum("ij,ij->i", nrm[fa], nrm[fb]), -1.0, 1.0)
    ang = np.degrees(np.arccos(dot))
    smooth = ang <= crease_deg

    fa, fb = fa[smooth], fb[smooth]
    g = sparse.coo_matrix((np.ones(len(fa), np.int8), (fa, fb)), shape=(n, n))
    ncomp, lbl = csgraph.connected_components(g, directed=False)
    return ncomp, lbl, ang


def patch_stats(verts, faces, lbl, ncomp):
    """Triangle count, centroid and bounds for every crease patch."""
    cnt = np.bincount(lbl, minlength=ncomp)
    fc = verts[faces].mean(axis=1)
    cent = np.zeros((ncomp, 3))
    lo = np.full((ncomp, 3), np.inf)
    hi = np.full((ncomp, 3), -np.inf)
    tv = verts[faces]
    for d in range(3):
        cent[:, d] = np.bincount(lbl, weights=fc[:, d], minlength=ncomp) / np.maximum(cnt, 1)
        np.minimum.at(lo[:, d], lbl, tv[:, :, d].min(axis=1))
        np.maximum.at(hi[:, d], lbl, tv[:, :, d].max(axis=1))
    return cnt, cent, lo, hi


# Depth at which a patch stops being a front-face control. Front controls
# run from the trackpads at Y -7 to the stick caps at Y +7; everything the
# front elevation cannot see sits at Y -15 or further back. Read off the
# patch table, not picked.
BACK_Y = -12.0


def name_back_patches(cnt, cent, lo, hi, eligible_patches):
    """Name the parts a front elevation cannot see, by where they are.

    The bumpers, the triggers and the four rear buttons have no silhouette
    in the drawing, so the projection that names every front control is
    blind to them, and two of them were being claimed by the trackpad
    masks they sit directly behind.

    CAD orientation, fixed by the drawing: X is width, Y is depth with the
    front positive, and Z is height measured DOWNWARD, so a smaller Z is
    higher up the pad.

    Each of these is a mirror-symmetric pair, which is what identifies
    them as controls rather than shell detail, and the pairs separate
    cleanly by height:

      z about   0   the top edge, two pairs. The one further back is the
                    trigger, the one in front of it the bumper.
      z about  54   upper rear buttons, R4 and L4.
      z about  74   lower rear buttons, R5 and L5.
    """
    cands = [i for i in eligible_patches if cnt[i] >= 4000 and cent[i][1] < BACK_Y]
    if not cands:
        return {}
    top = sorted([i for i in cands if cent[i][2] < 30.0], key=lambda i: cent[i][1])
    rear = sorted([i for i in cands if cent[i][2] >= 30.0], key=lambda i: cent[i][2])

    out = {}

    def side(i, left, right):
        out[i] = left if cent[i][0] < 0 else right

    # Top edge: the two most rearward are the triggers, the next two the
    # bumpers. Four entries expected, two per pair.
    for i in top[:2]:
        side(i, "Shoulder-Left-Trigger.obj", "Shoulder-Right-Trigger.obj")
    for i in top[2:4]:
        side(i, "L1.obj", "R1.obj")

    # Rear buttons: upper pair is 4, lower pair is 5. Handedness follows
    # the translator the Steam Deck set already uses, where R4 is Paddle1,
    # L4 is Paddle2, R5 is Paddle3 and L5 is Paddle4.
    for i in rear[:2]:
        side(i, "L4.obj", "R4.obj")
    for i in rear[2:4]:
        side(i, "L5.obj", "R5.obj")
    return out


def cluster(tris, cell):
    flat = tris.reshape(-1, 3)
    keys = np.floor(flat / cell).astype(np.int64)
    _, inv = np.unique(keys, axis=0, return_inverse=True)
    inv = np.asarray(inv).ravel()
    order = np.argsort(inv)
    si = inv[order]
    bounds = np.searchsorted(si, np.arange(si[-1] + 2))
    sums = np.add.reduceat(flat[order], bounds[:-1], axis=0)
    counts = np.diff(bounds).reshape(-1, 1)
    reps = sums / np.maximum(counts, 1)
    faces = inv.reshape(-1, 3)
    ok = ((faces[:, 0] != faces[:, 1]) & (faces[:, 1] != faces[:, 2])
          & (faces[:, 0] != faces[:, 2]))
    faces = faces[ok]
    if len(faces):
        _, first = np.unique(np.sort(faces, axis=1), axis=0, return_index=True)
        faces = faces[np.sort(first)]
    return reps, faces


def decimate(tris, budget):
    """Weld into an indexed mesh, then collapse edges by quadric error.

    NOT vertex clustering. Clustering snaps every vertex to a grid cell
    and replaces it with the cell's mean, which MOVES the surface and
    rounds off every sharp edge the moulding has. Rendered with the smooth
    normals a viewport computes, the result is soft and waxy no matter how
    many triangles it keeps.

    Quadric decimation only ever collapses an existing edge, and it
    chooses the collapse that changes the surface least, so flat regions
    lose triangles first and the creases survive. Valve's 2026 release is
    one clean manifold solid, which is exactly the input this wants.
    """
    verts, faces = weld(tris)
    if len(faces) <= budget:
        return verts, faces
    pv, pf = fs.simplify(verts, faces.astype(np.int32),
                         target_count=budget, agg=7.0)
    return np.asarray(pv, np.float64), np.asarray(pf, np.int64)


def weld(tris, tol=1e-3):
    """Triangle soup to indexed mesh. An edge collapse needs shared
    vertices, and an STL shares none: every triangle stores its own three
    corners. The representative is a real input vertex, so nothing moves.
    """
    q = np.round(tris.reshape(-1, 3) / tol).astype(np.int64)
    uniq, inv = np.unique(q, axis=0, return_inverse=True)
    inv = np.asarray(inv).ravel()
    first = np.zeros(len(uniq), np.int64)
    first[inv[::-1]] = np.arange(len(inv))[::-1]
    verts = tris.reshape(-1, 3)[first]
    faces = inv.reshape(-1, 3)
    ok = ((faces[:, 0] != faces[:, 1]) & (faces[:, 1] != faces[:, 2])
          & (faces[:, 0] != faces[:, 2]))
    return verts, faces[ok]


def write_obj(path, verts, faces, name):
    """Write the part in PadForge model space.

    The axis map is (x, -y, -z), two sign flips, which is a rotation about
    X rather than a mirror, so the winding is left alone.

    Y is then shifted so the pad straddles the camera axis the way its
    siblings do. Valve put the CAD origin on the front face, which would
    park the whole controller behind the axis; the view recentres Z on
    load but nothing recentres Y, so it has to be right in the mesh. The
    shipped Xbox 360 body runs Y -20 to +34 and the 2015 Steam Controller
    -23 to +34, so centring this body on its own mid-depth lands it in the
    same place as both.
    """
    out = np.column_stack([verts[:, 0], -verts[:, 1] - Y_SHIFT, -verts[:, 2]])
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("# %s\n# Valve Steam Controller (2026) CAD, CC BY-NC-SA 4.0\n" % name)
        for v in out:
            f.write("v %.4f %.4f %.4f\n" % (v[0], v[1], v[2]))
        f.write("g %s\n" % os.path.splitext(name)[0])
        for a, b, c in faces:
            f.write("f %d %d %d\n" % (a + 1, b + 1, c + 1))


def main():
    os.makedirs(DST, exist_ok=True)
    tris = read_stl(STL)
    print(f"{len(tris):,} triangles from {os.path.basename(STL)}")
    verts, faces = weld(tris)
    ncomp, lbl, _ = segment(verts, faces, CREASE_DEG)
    cnt, cent, lo, hi = patch_stats(verts, faces, lbl, ncomp)
    d = {"lo": lo, "hi": hi}
    print(f"{len(faces):,} triangles in {len(cnt):,} crease patches")

    masks = control_masks()
    h, w = next(iter(masks.values())).shape
    print(f"control silhouettes: {len(masks)} on a {w}x{h} sheet")

    # Assign PER TRIANGLE, but only inside patches the crease pass already
    # separated from the body. Two jobs, each done by the source that can
    # do it:
    #
    #   the crease patches decide what is NOT body, so a triangle of shell
    #   that merely happens to sit under a button is never stolen,
    #
    #   the projection decides WHICH control, so one patch spanning several
    #   controls splits correctly. The D-pad is exactly that case: the
    #   whole cross comes out as a single patch, and assigning it by its
    #   centroid put all four arms into DPadUp.
    body_patch = int(np.argmax(cnt))
    eligible = lbl != body_patch

    # Name the back-of-pad parts first and take them out of the front
    # projection's reach. Two of them sit directly behind the trackpads
    # and were being claimed by those silhouettes.
    back = name_back_patches(cnt, cent, d["lo"], d["hi"],
                             [i for i in range(len(cnt)) if i != body_patch])
    back_groups = {}
    for patch, part in back.items():
        sel = lbl == patch
        back_groups[part] = back_groups.get(part, np.zeros(len(faces), bool)) | sel
        eligible &= ~sel
    fc = verts[faces].mean(axis=1)
    px, py = cad_to_px(fc[:, 0], fc[:, 2])
    ix = np.clip(np.rint(px).astype(int), 0, w - 1)
    iy = np.clip(np.rint(py).astype(int), 0, h - 1)

    claimed = np.zeros(len(faces), bool)
    groups = dict(back_groups)
    for part, sel in sorted(back_groups.items()):
        claimed |= sel
        print(f"  {part:26s} {int(sel.sum()):8,} tris (rear, by position)")
    # Smallest silhouette first, so the more specific control claims
    # its geometry before a larger one that encloses it. The stick cap
    # sits inside the stick ring, and the ring's own hole IS the cap,
    # so filling holes makes the ring a full disc: iterated first it
    # swallowed both caps and left the click meshes empty.
    for name, m in sorted(masks.items(), key=lambda kv: int(kv[1].sum())):
        part = PART[name]
        sel = eligible & ~claimed & m[iy, ix]
        if not sel.any():
            print(f"  {part:26s} NO GEOMETRY")
            continue
        claimed |= sel
        groups[part] = groups.get(part, np.zeros(len(faces), bool)) | sel
        print(f"  {part:26s} {int(sel.sum()):8,} tris")
    groups["MainBody.obj"] = ~claimed
    print(f"  {'MainBody.obj':26s} {int((~claimed).sum()):8,} tris (leftover)")

    # Centre the assembly on its body's mid-depth before anything is
    # written, so every part moves together.
    global Y_SHIFT
    body_y = -verts[faces[groups["MainBody.obj"]]][:, :, 1]
    Y_SHIFT = float((body_y.min() + body_y.max()) / 2.0)
    print(f"  Y shift {Y_SHIFT:+.2f} mm to centre the body on the camera axis")

    total_in = total_out = 0
    for part, sel in sorted(groups.items()):
        tris = verts[faces[sel]]
        if not len(tris):
            print(f"  SKIP {part}: empty")
            continue
        v, f = decimate(tris, BUDGET.get(part, DEFAULT_BUDGET))
        write_obj(os.path.join(DST, part), v, f, part)
        total_in += len(tris)
        total_out += len(f)
        print(f"  {part:26s} {len(tris):9,} -> {len(f):7,} tris  ({len(v):,} verts)")
    print(f"TOTAL {total_in:,} -> {total_out:,} triangles")


if __name__ == "__main__":
    main()
