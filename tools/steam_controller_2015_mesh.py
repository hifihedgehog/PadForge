"""Rebuild the 2015 Steam Controller as a clean outer skin.

Usage:
    pip install numpy scipy scikit-image fast-simplification
    set SC2015_STL_DIR=<the unpacked "STL of separate parts" archive>
    python tools/steam_controller_2015_mesh.py

Why this and not decimation of the CAD. Valve's 2015 release is 22
moulded parts carrying every internal rib, boss and screw tower, 29.7
million triangles of it. Two things follow, both measured rather than
assumed:

* Vertex clustering welds the outer wall to the ribs behind it, because
  at the cell size a shippable budget implies the cell spans the wall.
  That is what turned the case into faceted noise.
* Quadric decimation cannot get low enough. It respects the mesh it is
  given, and this mesh has 26,000 non-manifold edges scattered through
  its interior structure, so the case top asymptotes at about 314,000
  triangles no matter how many passes it runs.

The interior is the problem in both cases, and it is not wanted: the
preview shows the outside of a controller. So rather than fight to remove
it, this rebuilds the outside directly. Voxelize the assembled
controller, flood the air around it, and take the boundary between the
two as a fresh surface. What comes out is watertight, manifold, has no
interior at all, and decimates to any budget cleanly.

The cost is that marching cubes rounds a sharp edge by about one voxel.
At a third of a millimetre on a 160 mm controller that is not visible in
a preview, and it buys a body that reads as the moulding it is.

Parts stay separate: each output triangle is assigned to whichever
original CAD part is nearest, so the buttons, pads and grips remain their
own meshes and stay individually mappable.
"""
import numpy as np
import struct
import os
import io
import sys
from scipy import ndimage as nd
from scipy.spatial import cKDTree
from skimage import measure
import fast_simplification as fs

SRC = os.environ.get("SC2015_STL_DIR", r"C:/tmp/sc2015")
DST = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "PadForge.App", "3DModels", "SteamController")

VOXEL = 0.30            # mm. Roughly a third of the smallest feature radius.
SKIN_BUDGET = 120000
SEAL = 2                # voxels of closing, only to bridge sub-voxel seams

MERGE_BODY = [
    "CaseTopGPrime.rev01.01.stl",
    "CaseFrontGPrime.rev01.01.stl",
    "CaseBottomGPrime.rev01.01.stl",
    "BatteryDoorMkVI.rev01.01.stl",
]
# CAD part -> PadForge part file. The body shells all collapse into one
# mesh because the skin does not distinguish them anyway.
MAP = {
    "ButtonA.rev01.01.stl":                     "B1.obj",
    "ButtonB.rev01.01.stl":                     "B2.obj",
    "ButtonX.rev01.01.stl":                     "B3.obj",
    "ButtonY.rev01.01.stl":                     "B4.obj",
    "ButtonSteam-IML.rev01.01.stl":             "Special.obj",
    "ButtonStart.rev01.01.stl":                 "Start.obj",
    "ButtonSelect.rev01.01.stl":                "Back.obj",
    "TriggerCapLeftJAG.rev01.01.stl":           "Shoulder-Left-Trigger.obj",
    "TriggerCapRightJAG.rev01.01.stl":          "Shoulder-Right-Trigger.obj",
    "TrackPadCoverDirectional.01.rev01.01.stl": "LeftPadTouch.obj",
    "TrackPadCoverSmooth.01.rev01.01.stl":      "RightPadTouch.obj",
    "ThumbTop29.rev01.01.stl":                  "LeftStickClick.obj",
    "BatteryLeverLeft.rev01.01.stl":            "LeftGrip.obj",
    "BatteryLeverRight.rev01.01.stl":           "RightGrip.obj",
}


def read_stl(path):
    with open(path, "rb") as f:
        head = f.read(84)
        n = struct.unpack("<I", head[80:84])[0]
        raw = np.frombuffer(f.read(n * 50), np.uint8)
    n = min(n, raw.size // 50)
    rec = raw[:n * 50].reshape(n, 50)
    return np.frombuffer(rec[:, 12:48].tobytes(), "<f4").reshape(n, 3, 3).astype(np.float64)


def occupancy(tris, lo, dims):
    """Mark every voxel the surface passes through.

    Marking only triangle CORNERS is not enough, and that mistake is what
    made every earlier attempt leak: a CAD tessellator meshes a flat
    region with a few very large triangles, so a corner-only occupancy
    leaves metre-wide holes in the middle of the flattest parts of the
    case. The flood then walks straight through the shell and the
    reconstruction ends up with both faces of every wall.

    So each triangle is sampled across its face on a barycentric grid at
    half a voxel, and only triangles big enough to need it pay for it.
    """
    occ = np.zeros(dims, bool)
    dimarr = np.array(dims)

    def mark(pts):
        idx = np.floor((pts - lo) / VOXEL).astype(np.int64)
        np.clip(idx, 0, dimarr - 1, out=idx)
        occ[idx[:, 0], idx[:, 1], idx[:, 2]] = True

    mark(tris.reshape(-1, 3))

    e = np.stack([
        np.linalg.norm(tris[:, 1] - tris[:, 0], axis=1),
        np.linalg.norm(tris[:, 2] - tris[:, 1], axis=1),
        np.linalg.norm(tris[:, 0] - tris[:, 2], axis=1)]).max(axis=0)
    big = tris[e > VOXEL * 0.7]
    if not len(big):
        return occ

    # One barycentric lattice per subdivision level, so triangles of
    # similar size share a single vectorized pass.
    lvl = np.clip(np.ceil(e[e > VOXEL * 0.7] / (VOXEL * 0.5)).astype(int), 2, 64)
    for n in np.unique(lvl):
        sel = big[lvl == n]
        i, j = np.meshgrid(np.arange(n + 1), np.arange(n + 1))
        keep = (i + j) <= n
        u = (i[keep] / n)[None, :, None]
        v = (j[keep] / n)[None, :, None]
        w = 1.0 - u - v
        pts = (sel[:, 0][:, None, :] * w + sel[:, 1][:, None, :] * u
               + sel[:, 2][:, None, :] * v)
        mark(pts.reshape(-1, 3))
    return occ


def build_skin(parts):
    all_pts = np.concatenate([t.reshape(-1, 3) for t in parts.values()])
    lo = all_pts.min(0) - VOXEL * 4
    hi = all_pts.max(0) + VOXEL * 4
    dims = tuple(np.ceil((hi - lo) / VOXEL).astype(int) + 1)
    print(f"  grid {dims} = {np.prod(dims)/1e6:.1f}M voxels")

    occ = occupancy(np.concatenate(list(parts.values())), lo, dims)
    print(f"  surface voxels {int(occ.sum()):,}")

    # Seal, flood, then push the boundary back.
    #
    # The gaps around the buttons, the triggers and the case seam are
    # wider than a voxel, so a flood over the raw occupancy runs straight
    # into the shell and comes out the other side, and marching cubes
    # then reconstructs BOTH faces of every wall. Measured: sealing by
    # two voxels left only 1.4 million interior voxels, barely more than
    # the surface itself.
    #
    # So the flood runs against a heavily closed copy, which reliably
    # stays outside the shell, and the region it found is then dilated by
    # the same amount to recover the true surface position. Dilating back
    # can never eat into real surface voxels because those are masked out.
    st = nd.generate_binary_structure(3, 3)
    solid = nd.binary_closing(occ, st, iterations=SEAL)

    lbl, _ = nd.label(~solid)
    border = set(np.unique(np.concatenate([
        lbl[0].ravel(), lbl[-1].ravel(), lbl[:, 0].ravel(), lbl[:, -1].ravel(),
        lbl[:, :, 0].ravel(), lbl[:, :, -1].ravel()])))
    border.discard(0)
    outside = np.isin(lbl, list(border))
    outside = nd.binary_dilation(outside, st, iterations=SEAL) & ~occ
    inside = ~outside

    # Fill the cavity. Without this the reconstruction returns the INNER
    # face of every wall as well as the outer one, the two sit a
    # millimetre or two apart, and after decimation they interpenetrate
    # and speckle the whole body. Only the outside is ever looked at, so
    # the controller is reconstructed as if it were solid.
    inside = nd.binary_fill_holes(inside)
    print(f"  inside voxels {int(inside.sum()):,} "
          f"({inside.sum() / inside.size * 100:.1f}% of grid)")

    # Marching cubes on the outside/inside field. Smoothing the field
    # first keeps the surface from stepping along voxel faces.
    field = nd.gaussian_filter(inside.astype(np.float32), 0.8)
    verts, faces, _, _ = measure.marching_cubes(field, level=0.5)
    verts = verts * VOXEL + lo
    print(f"  marching cubes: {len(verts):,} verts, {len(faces):,} faces")
    return verts, faces.astype(np.int32)


def write_obj(path, verts, faces, name):
    """Valve CAD is X width, Y height up, Z depth front. PadForge is X
    width, Y depth front NEGATIVE, Z height. Y = -Z and Z = Y, a rotation
    about X, so the winding is left alone."""
    out = np.column_stack([verts[:, 0], -verts[:, 2], verts[:, 1]])
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("# %s\n# Valve Steam Controller (2015) CAD, CC BY-NC-SA 4.0\n" % name)
        for v in out:
            f.write("v %.4f %.4f %.4f\n" % (v[0], v[1], v[2]))
        f.write("g %s\n" % os.path.splitext(name)[0])
        for a, b, c in faces:
            f.write("f %d %d %d\n" % (a + 1, b + 1, c + 1))


def main():
    os.makedirs(DST, exist_ok=True)

    parts = {}
    body = [read_stl(os.path.join(SRC, f)) for f in MERGE_BODY]
    parts["MainBody.obj"] = np.concatenate(body)
    bump = read_stl(os.path.join(SRC, "BumperGPrime.rev01.01.stl"))
    cx = bump[:, :, 0].mean(axis=1)
    parts["L1.obj"] = bump[cx < 0]
    parts["R1.obj"] = bump[cx >= 0]
    for src, dst in MAP.items():
        parts[dst] = read_stl(os.path.join(SRC, src))
    print(f"  {sum(len(t) for t in parts.values()):,} source triangles in {len(parts)} parts")

    verts, faces = build_skin(parts)

    if len(faces) > SKIN_BUDGET:
        pv, pf = fs.simplify(verts, faces, target_count=SKIN_BUDGET, agg=7.0)
        verts, faces = np.asarray(pv, np.float64), np.asarray(pf, np.int64)
        print(f"  decimated skin -> {len(faces):,} faces")

    # Assign each skin triangle to the CAD part nearest its centre. The
    # skin has no part identity of its own, and the buttons, pads and
    # grips have to stay separately mappable.
    names, samples = [], []
    for i, (name, tris) in enumerate(sorted(parts.items())):
        pts = tris.reshape(-1, 3)
        step = max(1, len(pts) // 60000)
        samples.append(pts[::step])
        names.append(name)
    owner_of_sample = np.concatenate(
        [np.full(len(s), i) for i, s in enumerate(samples)])
    tree = cKDTree(np.concatenate(samples))
    cen = verts[faces].mean(axis=1)
    _, nearest = tree.query(cen, workers=-1)
    owner = owner_of_sample[nearest]

    # Centre X on the assembly so the model sits on the camera axis.
    xmid = (verts[:, 0].min() + verts[:, 0].max()) / 2.0
    verts = verts.copy()
    verts[:, 0] -= xmid

    total = 0
    for i, name in enumerate(names):
        sel = owner == i
        if not sel.any():
            print(f"  {name:28s} EMPTY")
            continue
        f = faces[sel]
        used, inv = np.unique(f.ravel(), return_inverse=True)
        write_obj(os.path.join(DST, name), verts[used], inv.reshape(-1, 3), name)
        total += len(f)
        print(f"  {name:28s} {len(f):7,} tris ({len(used):,} verts)")
    print(f"TOTAL {total:,} triangles")


if __name__ == "__main__":
    main()
