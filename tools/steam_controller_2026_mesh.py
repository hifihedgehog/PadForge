"""Convert Valve's 2026 Steam Controller CAD into PadForge's per-part OBJs.

Usage:
    pip install cadquery-ocp numpy scipy opencv-python pymupdf
    python tools/steam_controller_2026_mesh.py
    (reads ../SteamControllerHardware/SC_solid_stp_20260429.stp, or SC2026_STEP)

Source. Valve published the 2026 pad as ONE merged solid: the STEP holds a
single MANIFOLD_SOLID_BREP with 848 unnamed faces under one product,
IBEX_SOLID, and the STL beside it welds to a single connected component.
Unlike the 2015 release there is no part decomposition to recover, so the
parts have to be found in the geometry.

Why the STEP and not the STL. Two shipped builds came from the STL, first
by vertex clustering and then by quadric decimation, and both read as
blocky with random hard edges on the grips and cracks along the D-pad.
Every one of those has the same root: the STL is 1.58 million fixed
triangles, so any shippable count means decimation, and decimating parts
SEPARATELY leaves every part boundary as a seam where the two sides
collapsed differently. The STEP is the exact surface. Meshed once at
preview density it gives 170k triangles with true edges, and a shared
edge discretisation across every B-rep face boundary, so there is nothing
to decimate and no seam to open.

Normals come from the B-rep surface at each node, not from the triangles.
Geometry-derived normals need a crease threshold, and on a curved grip
some facet pairs cross it and some do not, which is exactly the "random
edges on the handles" a viewer sees. A surface normal is exact: smooth
where the surface is smooth, and different across a boundary only where
the surfaces really meet at an angle.

Naming. Crease segmentation cuts the mesh into patches along sharp
dihedrals, which are the real part boundaries in CAD output. Each patch
is named by projecting it onto the front elevation of Valve's reference
drawing and asking which control silhouette it lands in; the silhouettes
come from the same code that builds the 2D asset, so both views share one
authority. The bumpers, triggers and four rear buttons have no silhouette
in a front view and are named by position.

Axes. The drawing fixes the orientation. X is width. Z is height measured
DOWNWARD, which is why the D-pad at Z 20 lands at the top of the sheet. Y
is depth with the front positive. PadForge wants X width, Y depth with
the front NEGATIVE, Z height up, so the map is (x, -y, -z), two sign
flips, a rotation about X, so the winding from the face orientation flags
is kept.
"""
import numpy as np
import os
import io
import sys
import time
import cv2
import scipy.ndimage as nd
from scipy import sparse
from scipy.sparse import csgraph

from OCP.STEPControl import STEPControl_Reader
from OCP.IFSelect import IFSelect_RetDone
from OCP.BRepMesh import BRepMesh_IncrementalMesh
from OCP.BRepLib import BRepLib_ToolTriangulatedShape
from OCP.TopExp import TopExp_Explorer
from OCP.TopAbs import TopAbs_FACE, TopAbs_REVERSED
from OCP.TopoDS import TopoDS
from OCP.TopLoc import TopLoc_Location
from OCP.BRep import BRep_Tool

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DST = os.path.join(PROJ_ROOT, "PadForge.App", "3DModels", "SteamController2")
GEN = os.path.join(PROJ_ROOT, "tools")
STEP = os.environ.get("SC2026_STEP", os.path.join(
    os.path.dirname(PROJ_ROOT), "SteamControllerHardware", "SC_solid_stp_20260429.stp"))

# Mesh density. Linear deflection is the chord error in mm; angular is the
# facet-to-facet bound in radians. 0.15 rad is 8.6 degrees, below what the
# eye resolves as a polygon on a preview-sized silhouette. Measured cost on
# this solid: 170k triangles, meshed in under a second.
LIN_DEFL = 0.2
ANG_DEFL = 0.15

# Crease threshold for segmentation. Adjacent facets on one smooth surface
# differ by at most ANG_DEFL (8.6 degrees), so 22 degrees cuts only real
# part boundaries.
CREASE_DEG = 22.0

# Front-elevation registration for the drawing, read off the STL's bounds
# when the 2D asset was built. main() measures the STEP against the same
# numbers and refuses to run if they drift, since every name depends on
# this projection.
PAD_PT, DPI, SS = 6.0, 468.0, 4
BODY_PT_W, BODY_PT_H = 900.0, 627.9
PX_PER_PT = DPI / 72.0 / SS
OFF = PAD_PT * PX_PER_PT
SPAN_X, SPAN_Y = BODY_PT_W * PX_PER_PT, BODY_PT_H * PX_PER_PT
CAD_X0, CAD_W = -79.4, 158.8
CAD_Z0, CAD_H = -5.8, 110.8

# Depth at which a patch stops being a front-face control. Front controls
# run from the trackpads at Y -7 to the stick caps at Y +7; everything the
# front elevation cannot see sits at Y -15 or further back.
BACK_Y = -12.0

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


def cad_to_px(x, z):
    """Front-elevation pixel for a CAD (X, Z), in final asset pixels."""
    return (OFF + (x - CAD_X0) / CAD_W * SPAN_X,
            OFF + (z - CAD_Z0) / CAD_H * SPAN_Y)


# ----------------------------------------------------------------------
#  STEP in, triangles with surface normals out
# ----------------------------------------------------------------------

def load_step(path):
    r = STEPControl_Reader()
    if r.ReadFile(path) != IFSelect_RetDone:
        raise SystemExit(f"cannot read {path}")
    r.TransferRoots()
    return r.OneShape()


def tessellate(shape, lin_defl, ang_defl):
    """Mesh the solid. Returns verts, per-node surface normals, faces with
    outward winding, and the B-rep face id of every triangle.

    Nodes are per B-rep face, so a vertex on a boundary between two faces
    exists once per face with each face's own normal. That is what makes a
    tangent boundary shade smooth and a sharp one shade sharp with no
    threshold involved. BRepMesh discretises the shared edge identically
    on both faces, so the positions coincide and no crack opens.
    """
    BRepMesh_IncrementalMesh(shape, lin_defl, False, ang_defl, True)
    verts, norms, faces, fid = [], [], [], []
    base = 0
    ex = TopExp_Explorer(shape, TopAbs_FACE)
    k = 0
    while ex.More():
        face = TopoDS.Face_s(ex.Current())
        loc = TopLoc_Location()
        tri = BRep_Tool.Triangulation_s(face, loc)
        if tri is not None:
            BRepLib_ToolTriangulatedShape.ComputeNormals_s(face, tri)
            trsf = loc.Transformation()
            rev = face.Orientation() == TopAbs_REVERSED
            n = tri.NbNodes()
            for i in range(1, n + 1):
                p = tri.Node(i).Transformed(trsf)
                d = tri.Normal(i).Transformed(trsf)
                verts.append((p.X(), p.Y(), p.Z()))
                s = -1.0 if rev else 1.0
                norms.append((s * d.X(), s * d.Y(), s * d.Z()))
            for i in range(1, tri.NbTriangles() + 1):
                a, b, c = tri.Triangle(i).Get()
                if rev:
                    b, c = c, b
                faces.append((base + a - 1, base + b - 1, base + c - 1))
                fid.append(k)
            base += n
        k += 1
        ex.Next()
    return (np.array(verts, np.float64), np.array(norms, np.float64),
            np.array(faces, np.int64), np.array(fid, np.int64))


# ----------------------------------------------------------------------
#  Crease segmentation, on a welded copy used only for adjacency
# ----------------------------------------------------------------------

def weld_index(verts, tol=1e-3):
    """Merge coincident nodes across face boundaries, for adjacency only.
    Returns the merged positions AND the map from node to merged id. The
    two go together: a face array remapped by the ids must be looked up
    in the merged positions, never in the original array. Doing the
    latter fed garbage normals into the crease segmentation and every
    rear part went unnamed."""
    q = np.round(verts / tol).astype(np.int64)
    uniq, inv = np.unique(q, axis=0, return_inverse=True)
    inv = np.asarray(inv).ravel()
    first = np.zeros(len(uniq), np.int64)
    first[inv[::-1]] = np.arange(len(inv))[::-1]
    return verts[first], inv


def segment(verts, faces, crease_deg):
    n = len(faces)
    e = np.concatenate([faces[:, [0, 1]], faces[:, [1, 2]], faces[:, [2, 0]]])
    e.sort(axis=1)
    owner = np.tile(np.arange(n), 3)
    key = e[:, 0].astype(np.int64) * (int(faces.max()) + 1) + e[:, 1]
    order = np.argsort(key, kind="stable")
    key, owner = key[order], owner[order]
    same = key[1:] == key[:-1]
    i = np.nonzero(same)[0]
    fa, fb = owner[i], owner[i + 1]

    fn = np.cross(verts[faces[:, 1]] - verts[faces[:, 0]],
                  verts[faces[:, 2]] - verts[faces[:, 0]])
    fn /= np.maximum(np.linalg.norm(fn, axis=1, keepdims=True), 1e-12)
    dot = np.clip(np.einsum("ij,ij->i", fn[fa], fn[fb]), -1.0, 1.0)
    smooth = np.degrees(np.arccos(dot)) <= crease_deg
    fa, fb = fa[smooth], fb[smooth]
    g = sparse.coo_matrix((np.ones(len(fa), np.int8), (fa, fb)), shape=(n, n))
    ncomp, lbl = csgraph.connected_components(g, directed=False)
    return ncomp, lbl


def patch_stats(verts, faces, lbl, ncomp):
    cnt = np.bincount(lbl, minlength=ncomp)
    fc = verts[faces].mean(axis=1)
    cent = np.zeros((ncomp, 3))
    for d in range(3):
        cent[:, d] = np.bincount(lbl, weights=fc[:, d], minlength=ncomp) / np.maximum(cnt, 1)
    return cnt, cent


# ----------------------------------------------------------------------
#  Naming
# ----------------------------------------------------------------------

def control_masks():
    """The named control silhouettes, from the same code that builds the
    2D asset, so the two never drift apart."""
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
    out = {}
    for k, m in masks.items():
        small = cv2.resize(m.astype(np.uint8), (m.shape[1] // SS, m.shape[0] // SS),
                           interpolation=cv2.INTER_NEAREST).astype(bool)
        # A face-button silhouette is the disc MINUS its stroked glyph, and
        # an unfilled disc rejects points at its exact centre, which is
        # where the button's geometry projects.
        small = nd.binary_fill_holes(small)
        out[k] = nd.binary_dilation(small, iterations=3)
    return out


def name_back_patches(cnt, cent, eligible):
    """Name the parts a front elevation cannot see, by where they are.

    CAD orientation: X width, Y depth with the front positive, Z height
    measured DOWNWARD. Each of these is a mirror pair, which is what marks
    it as a control rather than shell detail, and the pairs separate by
    height: z near 0 is the top edge (trigger behind, bumper in front),
    z near 54 the upper rear buttons R4/L4, z near 74 the lower R5/L5.
    """
    cands = [i for i in eligible if cnt[i] >= 800 and cent[i][1] < BACK_Y]
    top = sorted([i for i in cands if cent[i][2] < 30.0], key=lambda i: cent[i][1])
    rear = sorted([i for i in cands if cent[i][2] >= 30.0], key=lambda i: cent[i][2])
    out = {}

    def side(i, left, right):
        out[i] = left if cent[i][0] < 0 else right

    for i in top[:2]:
        side(i, "Shoulder-Left-Trigger.obj", "Shoulder-Right-Trigger.obj")
    for i in top[2:4]:
        side(i, "L1.obj", "R1.obj")
    # Handedness follows the translator the Steam Deck set uses: R4 is
    # Paddle1, L4 Paddle2, R5 Paddle3, L5 Paddle4.
    for i in rear[:2]:
        side(i, "L4.obj", "R4.obj")
    for i in rear[2:4]:
        side(i, "L5.obj", "R5.obj")
    return out


# ----------------------------------------------------------------------
#  Output
# ----------------------------------------------------------------------

Y_SHIFT = 0.0


def write_obj(path, verts, norms, faces, name):
    """Map (x, -y, -z), then shift Y so the pad straddles the camera axis
    the way its siblings do: the view recentres Z on load, never Y."""
    used = np.unique(faces.ravel())
    remap = np.full(len(verts), -1, np.int64)
    remap[used] = np.arange(len(used))
    v = verts[used]
    n = norms[used]
    f = remap[faces]
    out = np.column_stack([v[:, 0], -v[:, 1] - Y_SHIFT, -v[:, 2]])
    nout = np.column_stack([n[:, 0], -n[:, 1], -n[:, 2]])
    with io.open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("# %s\n# Valve Steam Controller (2026) CAD, CC BY-NC-SA 4.0\n" % name)
        for p in out:
            fh.write("v %.4f %.4f %.4f\n" % (p[0], p[1], p[2]))
        for p in nout:
            fh.write("vn %.4f %.4f %.4f\n" % (p[0], p[1], p[2]))
        fh.write("g %s\n" % os.path.splitext(name)[0])
        for a, b, c in f + 1:
            fh.write("f %d//%d %d//%d %d//%d\n" % (a, a, b, b, c, c))
    return len(f), len(used)


def main():
    os.makedirs(DST, exist_ok=True)
    t0 = time.time()
    shape = load_step(STEP)
    verts, norms, faces, fid = tessellate(shape, LIN_DEFL, ANG_DEFL)
    print(f"  {len(faces):,} triangles over {fid.max() + 1} B-rep faces "
          f"in {time.time() - t0:.0f}s")

    # The projection was registered against the STL. Refuse to name
    # anything if the STEP does not sit in the same box.
    lo, hi = verts.min(0), verts.max(0)
    drift = max(abs(lo[0] - CAD_X0), abs(hi[0] - lo[0] - CAD_W),
                abs(lo[2] - CAD_Z0), abs(hi[2] - lo[2] - CAD_H))
    print(f"  bounds X[{lo[0]:.1f},{hi[0]:.1f}] Y[{lo[1]:.1f},{hi[1]:.1f}] "
          f"Z[{lo[2]:.1f},{hi[2]:.1f}]  drift from registration {drift:.2f} mm")
    if drift > 0.5:
        raise SystemExit("STEP bounds do not match the drawing registration")

    wverts, widx = weld_index(verts)
    ncomp, lbl = segment(wverts, widx[faces], CREASE_DEG)
    cnt, cent = patch_stats(verts, faces, lbl, ncomp)
    print(f"  {ncomp:,} crease patches")

    masks = control_masks()
    h, w = next(iter(masks.values())).shape

    body_patch = int(np.argmax(cnt))
    eligible = lbl != body_patch

    back = name_back_patches(cnt, cent, [i for i in range(ncomp) if i != body_patch])
    groups = {}
    claimed = np.zeros(len(faces), bool)
    for patch, part in back.items():
        sel = lbl == patch
        groups[part] = groups.get(part, np.zeros(len(faces), bool)) | sel
        eligible &= ~sel
        claimed |= sel
    for part in sorted(groups):
        print(f"  {part:26s} {int(groups[part].sum()):8,} tris (rear, by position)")

    fc = verts[faces].mean(axis=1)
    px, py = cad_to_px(fc[:, 0], fc[:, 2])
    ix = np.clip(np.rint(px).astype(int), 0, w - 1)
    iy = np.clip(np.rint(py).astype(int), 0, h - 1)

    # Smallest silhouette first, so the stick cap claims its geometry
    # before the ring that encloses it.
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

    global Y_SHIFT
    body_y = -verts[faces[groups["MainBody.obj"]]][:, :, 1]
    Y_SHIFT = float((body_y.min() + body_y.max()) / 2.0)
    print(f"  Y shift {Y_SHIFT:+.2f} mm to centre the body on the camera axis")

    total = 0
    for part, sel in sorted(groups.items()):
        nf, nv = write_obj(os.path.join(DST, part), verts, norms, faces[sel], part)
        total += nf
        print(f"  {part:26s} {nf:7,} tris  ({nv:,} verts)")
    print(f"TOTAL {total:,} triangles in {time.time() - t0:.0f}s")


if __name__ == "__main__":
    main()
