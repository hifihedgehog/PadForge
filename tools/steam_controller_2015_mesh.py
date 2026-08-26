"""Convert Valve's 2015 Steam Controller CAD into PadForge's per-part OBJs.

Three earlier attempts and what each got wrong, because the reasons are
the whole design:

1. Vertex clustering. Snaps vertices to a grid and replaces each cell
   with its mean, so it MOVES the surface, welds the outer wall to the
   ribs behind it, and rounds every moulded edge. The case came out as
   faceted noise.
2. Quadric decimation on the raw CAD. Right algorithm, wrong input: the
   assembly carries every internal rib and boss, 26,000 non-manifold
   edges among them, and the case top asymptotes near 314,000 triangles
   however many passes it runs. Measured, not assumed.
3. Voxel reconstruction. Removes the interior perfectly and decimates to
   anything, but marching cubes rounds every edge by about a voxel, so
   the result is smooth where the moulding is sharp.

What works, measured on the case top and proven by rendering:

* NO outer-surface mask. It was deleting the trackpad rims and the
  button-well lips, which is where the mesh looked jagged. 87% of the
  case-top triangles sit within 0.7 mm of outside air; there is nothing
  worth removing and the decimator hides what little interior remains.
* Weld COARSE, scaled to the part. The "detail" a fine weld preserves is
  the tessellation seams themselves, and they are what pin the decimator
  above budget. 0.1 mm on the 160 mm case stitches them; a 9 mm cap gets
  proportionally less. Rendered at 0.1 mm the case is a clean shell.
* Split non-manifold edges ONCE, decimate ONCE, and never re-weld the
  result. The reweld was what shredded the caps and holed the body: it
  merged split copies back together with mixed winding.
* The decimator lands near budget rather than on it. The residual floor
  is a few hundred border edges quadric collapse will not touch, and
  chasing it with more passes made the mesh worse, not smaller.

Normals are written explicitly, split at a crease angle. Without them a
viewport averages normals across every face at a vertex, which rounds off
exactly the edges this whole pipeline exists to keep. Every model in this
repo that reads as moulded plastic ships normals; the ones that did not
read as clay.
"""
import numpy as np
import struct
import os
import io
import sys
import fast_simplification as fs

SRC = os.environ.get("SC2015_STL_DIR", r"C:/tmp/sc2015")
DST = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "PadForge.App", "3DModels", "SteamController")

WELD_MM_PER_MM = 1.0 / 900.0    # weld tolerance as a fraction of the part diagonal; 0.1 mm on the 160 mm case, the value that rendered clean
WELD_MIN, WELD_MAX = 0.01, 0.1
CREASE_DEG = 40.0

BUDGET = {
    "MainBody.obj": 46000,
    "LeftPadTouch.obj": 4000, "RightPadTouch.obj": 4000,
    "LeftStickClick.obj": 3500,
    "LeftGrip.obj": 3000, "RightGrip.obj": 3000,
    "Shoulder-Left-Trigger.obj": 3000, "Shoulder-Right-Trigger.obj": 3000,
    "L1.obj": 3000, "R1.obj": 3000,
    "Special.obj": 1600,
    "B1.obj": 1200, "B2.obj": 1200, "B3.obj": 1200, "B4.obj": 1200,
    "Start.obj": 900, "Back.obj": 900,
}
DEFAULT_BUDGET = 1500

MERGE_BODY = [
    "CaseTopGPrime.rev01.01.stl",
    "CaseFrontGPrime.rev01.01.stl",
    "CaseBottomGPrime.rev01.01.stl",
    "BatteryDoorMkVI.rev01.01.stl",
]
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


def split_nonmanifold(verts, faces):
    """Give every face its own vertex copy wherever an edge is shared by
    more than two faces. The decimator will not collapse across such an
    edge, and this input has tens of thousands of them from unstitched
    CAD tessellation. Split, they become ordinary borders it can work
    around, and the floor disappears."""
    e = np.concatenate([faces[:, [0, 1]], faces[:, [1, 2]], faces[:, [2, 0]]])
    e.sort(axis=1)
    _, inv, cnt = np.unique(e, axis=0, return_inverse=True, return_counts=True)
    bad = cnt[np.asarray(inv).ravel()] > 2
    badv = np.unique(e[bad].ravel())
    mask = np.isin(faces, badv)
    if not mask.any():
        return verts, faces
    fi, fk = np.nonzero(mask)
    newv = np.concatenate([verts, verts[faces[fi, fk]]])
    faces = faces.copy()
    faces[fi, fk] = len(verts) + np.arange(len(fi))
    return newv, faces


def weld(tris, tol=WELD_MAX):
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


def decimate(tris, budget):
    pts = tris.reshape(-1, 3)
    diag = float(np.linalg.norm(pts.max(0) - pts.min(0)))
    tol = float(np.clip(diag * WELD_MM_PER_MM, WELD_MIN, WELD_MAX))
    v, f = weld(tris, tol)
    if len(f) <= budget or len(f) < 32:
        return v, f
    # ONE split-and-decimate round. It lands about double the budget on
    # the body and the stick cap, and that is accepted: a second round
    # gets closer to the number and visibly worse, cratering the cap and
    # putting facets back into the case. The single-round output rendered
    # clean, and shape beats count.
    v, f = split_nonmanifold(v, f)
    pv, pf = fs.simplify(v, f.astype(np.int32), target_count=budget, agg=7.0)
    return np.asarray(pv, np.float64), np.asarray(pf, np.int64)


def orient_faces(verts, faces):
    """Make every face wind the same way as its neighbours.

    The split-and-reweld leaves faces with inconsistent winding: a seam
    face and its neighbour can traverse their shared edge in the SAME
    direction, which means one of them is flipped. A flipped face has a
    flipped normal, so the crease-normal splitter sees a 180 degree
    crease where the surface is smooth and fractures the shading into
    speckle. Measured on the case: 2,259 same-direction edge pairs; on a
    9 mm button cap nearly half the vertices split into two or more
    normal groups on what is a smooth dome.

    Flood-fill orientation over the edge adjacency: pick a face, and for
    each neighbour make it traverse the shared edge the opposite way,
    flipping it if not. Each connected patch is then oriented as a whole
    to point away from the part's centroid.
    """
    n = len(faces)
    e = np.concatenate([faces[:, [0, 1]], faces[:, [1, 2]], faces[:, [2, 0]]])
    owner = np.tile(np.arange(n), 3)
    key = np.minimum(e[:, 0], e[:, 1]).astype(np.int64) * len(verts) + np.maximum(e[:, 0], e[:, 1])
    order = np.argsort(key, kind="stable")
    key, owner, e = key[order], owner[order], e[order]
    same = np.nonzero(key[1:] == key[:-1])[0]
    fa, fb = owner[same], owner[same + 1]
    # same_dir: both faces traverse the edge in the same direction (one is flipped)
    same_dir = (e[same, 0] == e[same + 1, 0])
    from collections import defaultdict
    adj = defaultdict(list)
    for a, b, sd in zip(fa, fb, same_dir):
        adj[a].append((b, sd)); adj[b].append((a, sd))
    flip = np.zeros(n, bool); seen = np.zeros(n, bool)
    faces = faces.copy()
    for start in range(n):
        if seen[start]:
            continue
        stack = [start]; seen[start] = True; patch = [start]
        while stack:
            cur = stack.pop()
            for nb, sd in adj[cur]:
                if seen[nb]:
                    continue
                # relative to cur's (possibly already flipped) state
                flip[nb] = flip[cur] ^ sd
                seen[nb] = True; stack.append(nb); patch.append(nb)
        patch = np.array(patch)
        faces[patch[flip[patch]]] = faces[patch[flip[patch]]][:, [0, 2, 1]]
        # orient the whole patch outward
        tv = verts[faces[patch]]
        nrm = np.cross(tv[:, 1] - tv[:, 0], tv[:, 2] - tv[:, 0])
        cen = tv.mean(axis=1)
        out = (nrm * (cen - verts.mean(0))).sum(axis=1)
        if out.sum() < 0:
            faces[patch] = faces[patch][:, [0, 2, 1]]
    return faces


def crease_normals(verts, faces, crease_deg=CREASE_DEG):
    """Per-corner normals, averaged only within the crease angle so a
    vertex on a sharp edge keeps a different normal for each side."""
    fn = np.cross(verts[faces[:, 1]] - verts[faces[:, 0]],
                  verts[faces[:, 2]] - verts[faces[:, 0]])
    ln = np.linalg.norm(fn, axis=1, keepdims=True)
    fn = fn / np.maximum(ln, 1e-20)
    area = ln[:, 0]
    cos_lim = np.cos(np.radians(crease_deg))

    corner_v = faces.ravel()
    corner_f = np.repeat(np.arange(len(faces)), 3)
    order = np.argsort(corner_v, kind="stable")
    corner_v, corner_f = corner_v[order], corner_f[order]
    starts = np.searchsorted(corner_v, np.arange(len(verts) + 1))

    normals = []
    corner_normal = np.zeros((len(faces), 3), np.int64)
    for v in range(len(verts)):
        a, b = starts[v], starts[v + 1]
        if a == b:
            continue
        groups = []
        for f in corner_f[a:b]:
            n = fn[f]
            for g in groups:
                if float(n @ g[0]) >= cos_lim:
                    g[1] += n * area[f]
                    g[2].append(f)
                    break
            else:
                groups.append([n, n * area[f], [f]])
        for rep, acc, fl in groups:
            m = np.linalg.norm(acc)
            normals.append(acc / m if m > 1e-20 else rep)
            k = len(normals)
            for f in fl:
                corner_normal[f, np.nonzero(faces[f] == v)[0][0]] = k
    return np.array(normals), corner_normal


def write_obj(path, verts, faces, name):
    """Valve CAD is X width, Y height up, Z depth front. PadForge is X
    width, Y depth with the front NEGATIVE, Z height. Y = -Z and Z = Y is
    a rotation about X, not a mirror, so the winding is left alone."""
    faces = orient_faces(verts, faces)
    normals, corner = crease_normals(verts, faces)
    out = np.column_stack([verts[:, 0], -verts[:, 2], verts[:, 1]])
    nout = np.column_stack([normals[:, 0], -normals[:, 2], normals[:, 1]])
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("# %s\n# Valve Steam Controller (2015) CAD, CC BY-NC-SA 4.0\n" % name)
        for v in out:
            f.write("v %.4f %.4f %.4f\n" % (v[0], v[1], v[2]))
        for n in nout:
            f.write("vn %.4f %.4f %.4f\n" % (n[0], n[1], n[2]))
        f.write("g %s\n" % os.path.splitext(name)[0])
        for i in range(len(faces)):
            a, b, c = faces[i] + 1
            na, nb, nc = corner[i]
            f.write("f %d//%d %d//%d %d//%d\n" % (a, na, b, nb, c, nc))


def main():
    os.makedirs(DST, exist_ok=True)
    parts = {"MainBody.obj": np.concatenate(
        [read_stl(os.path.join(SRC, f)) for f in MERGE_BODY])}
    bump = read_stl(os.path.join(SRC, "BumperGPrime.rev01.01.stl"))
    cx = bump[:, :, 0].mean(axis=1)
    parts["L1.obj"] = bump[cx < 0]
    parts["R1.obj"] = bump[cx >= 0]
    for src, dst in MAP.items():
        parts[dst] = read_stl(os.path.join(SRC, src))
    print(f"  {sum(len(t) for t in parts.values()):,} source triangles")

    allx = np.concatenate([t[:, :, 0].ravel() for t in parts.values()])
    xmid = (allx.min() + allx.max()) / 2.0

    total_in = total_out = 0
    for name, tris in sorted(parts.items()):
        kept = tris.copy()
        kept[:, :, 0] -= xmid
        v, f = decimate(kept, BUDGET.get(name, DEFAULT_BUDGET))
        write_obj(os.path.join(DST, name), v, f, name)
        total_in += len(kept)
        total_out += len(f)
        print(f"  {name:28s} {len(tris):9,} -> {len(f):6,} tris")
    print(f"TOTAL {total_in:,} -> {total_out:,} triangles")


if __name__ == "__main__":
    main()
