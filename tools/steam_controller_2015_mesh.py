"""Convert Valve's 2015 Steam Controller per-part CAD STLs into PadForge's
per-part OBJ set.

Usage:
    pip install numpy
    set SC2015_STL_DIR=<the unpacked "STL of separate parts" archive>
    python tools/steam_controller_2015_mesh.py

Source: Valve's March 2016 "STL of separate parts" archive, CC BY-NC-SA 4.0.
The parts already share one assembled coordinate space in millimetres, so no
alignment work is needed, only an axis remap and a very large decimation:
the raw set is 29.7 million triangles because it is a CAD tessellation, and
the shipped models in this repo are tens of thousands.

Three passes, in order. Each exists because the pass before it is not
enough on its own, and the reasoning is in write_obj and exterior.py.

1. Thin every shell at a cell far below the wall thickness, purely to make
   the next pass affordable.
2. One visibility pass over the ASSEMBLED controller, which drops the
   interiors of the mouldings. Run per part it does not work, because a
   case half on its own is open and you can see straight into it.
3. Decimate each part to its own triangle budget by vertex clustering,
   which is the right choice for smooth CAD surfaces and cannot open
   holes.
"""
import numpy as np
import struct, os, io

SRC = os.environ.get("SC2015_STL_DIR", r"C:/tmp/sc2015")
DST = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "PadForge.App", "3DModels", "SteamController")

# Valve part -> PadForge part filename. Several Valve parts merge into
# MainBody; the internals (PCB, membrane, slider) are dropped because the
# model is a shell the user looks at, not an exploded assembly.
MERGE_BODY = [
    "CaseTopGPrime.rev01.01.stl",
    "CaseFrontGPrime.rev01.01.stl",
    "CaseBottomGPrime.rev01.01.stl",
    "BatteryDoorMkVI.rev01.01.stl",
]
MAP = {
    "ButtonA.rev01.01.stl":                    "B1.obj",
    "ButtonB.rev01.01.stl":                    "B2.obj",
    "ButtonX.rev01.01.stl":                    "B3.obj",
    "ButtonY.rev01.01.stl":                    "B4.obj",
    "ButtonSteam-IML.rev01.01.stl":            "Special.obj",
    "ButtonStart.rev01.01.stl":                "Start.obj",
    "ButtonSelect.rev01.01.stl":               "Back.obj",
    "TriggerCapLeftJAG.rev01.01.stl":          "Shoulder-Left-Trigger.obj",
    "TriggerCapRightJAG.rev01.01.stl":         "Shoulder-Right-Trigger.obj",
    "TrackPadCoverDirectional.01.rev01.01.stl":"LeftPadTouch.obj",
    "TrackPadCoverSmooth.01.rev01.01.stl":     "RightPadTouch.obj",
    "ThumbTop29.rev01.01.stl":                 "LeftStickClick.obj",
    "BatteryLeverLeft.rev01.01.stl":           "LeftGrip.obj",
    "BatteryLeverRight.rev01.01.stl":          "RightGrip.obj",
    "Slider.rev01.01.stl":                     None,   # internal
    "PCBMain.rev01.01.stl":                    None,   # internal
    "MembraneAssyFreeman.rev01.01.stl":        None,   # internal
}

# Triangle budget per output part. The body carries the silhouette so it
# gets the lion's share; a thumbstick cap does not need 11.8 million
# triangles.
#
# The body's 60,000 was measured, not guessed. This case is covered in
# 2 to 3 mm detail (the trackpad wells, the button apertures, the seams),
# and at 26,000 the cell is about that size, so those features come out as
# noise. Rendered side by side, 26,000 is a wreck, 60,000 is clean, and
# 100,000 is barely different from 60,000. It puts the set at roughly 87k
# triangles against the Steam Deck's 55k, which is a fair price for a body
# that reads as the pad it is.
BUDGET = {
    "MainBody.obj": 60000,
    "BumperLeft.obj": 3000, "BumperRight.obj": 3000,
    "Shoulder-Left-Trigger.obj": 2500, "Shoulder-Right-Trigger.obj": 2500,
    "LeftPadTouch.obj": 3000, "RightPadTouch.obj": 3000,
    "LeftStickClick.obj": 2500,
    "LeftGrip.obj": 2500, "RightGrip.obj": 2500,
    "Special.obj": 1200,
    "B1.obj": 900, "B2.obj": 900, "B3.obj": 900, "B4.obj": 900,
    "Start.obj": 700, "Back.obj": 700,
}
DEFAULT_BUDGET = 1500

# Both well under the ~2 mm wall of these mouldings, so neither can
# weld the outer surface to what sits behind it.
PREPASS_CELL = 0.35
EXTERIOR_CELL = 0.5


# ----------------------------------------------------------------------
#  Visible-surface extraction
# ----------------------------------------------------------------------
#
# Reduce a CAD part to the surface someone can actually see.
# 
# Why this exists. Valve's 2015 parts are injection-moulded case halves and
# caps: behind the visible wall sit ribs, screw towers, snap features and
# the whole inside of the moulding. A case half is an OPEN shell, so a
# connectivity test cannot tell that geometry apart from the outside; the
# flood reaches it through the open rim, which is why a cavity test found
# nothing to drop.
# 
# That interior is what wrecks the decimation. Getting a 6.5 million
# triangle case half down to a shippable budget means cells wide enough to
# span the wall, and a cell holding both the outer surface and a rib behind
# it welds them into one crumpled sheet. The result reads as shattered
# geometry rather than a controller.
# 
# Method: visible-surface extraction, run on the WHOLE ASSEMBLY at once.
# Look at it from a spread of directions and from each one keep only the
# front-most triangles in every cell of the image plane. The union over all
# directions is the outer skin.
# 
# Running it per part does not work, and the reason is worth stating: a
# case half on its own is open, so from below you look straight into it and
# its ribs really are the nearest surface. Assembled, the other half covers
# that opening, and the ribs are behind something from every direction
# outside. Occlusion is a property of the assembly, not of the part.
# 
# The 2026 pad does not need this: Valve published it as one solid with no
# interior, which is why the same decimation gives a clean mesh there.

def _directions(n_ring=8):
    """A spread of viewing directions covering the sphere.

    Six axes plus the eight corners plus a ring around the equator. The
    equator ring matters most: a pad is looked at from the front and the
    sides, and grazing angles are where a missing skin triangle shows.
    """
    dirs = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)]
    for sx in (-1, 1):
        for sy in (-1, 1):
            for sz in (-1, 1):
                dirs.append((sx, sy, sz))
    for i in range(n_ring):
        a = 2 * np.pi * i / n_ring
        for tilt in (-0.5, 0.0, 0.5):
            dirs.append((np.cos(a), np.sin(a), tilt))
    d = np.array(dirs, float)
    return d / np.linalg.norm(d, axis=1, keepdims=True)


# Depth band kept behind the nearest surface, in millimetres. Keeping
# only the single nearest triangle per cell leaves the skin full of
# holes, because one triangle rarely covers a whole cell. A band about
# half the wall thickness keeps the outer surface contiguous while still
# excluding ribs and towers, which sit several millimetres behind it.
DEPTH_BAND = 0.3


def visible_surface(tris, cell):
    """Keep the triangles within the front depth band from some direction."""
    cen = tris.mean(axis=1)
    keep = np.zeros(len(tris), bool)

    for d in _directions():
        # Any two axes orthogonal to the view direction form the image plane.
        tmp = np.array([0.0, 0.0, 1.0]) if abs(d[2]) < 0.9 else np.array([1.0, 0.0, 0.0])
        u = np.cross(d, tmp)
        u /= np.linalg.norm(u)
        v = np.cross(d, u)

        pu = cen @ u
        pv = cen @ v
        depth = cen @ d

        iu = np.floor((pu - pu.min()) / cell).astype(np.int64)
        iv = np.floor((pv - pv.min()) / cell).astype(np.int64)
        nu = iu.max() + 1
        flat = iv * nu + iu

        # Nearest to the viewer is the smallest depth along d.
        best = np.full(int(flat.max()) + 1, np.inf)
        np.minimum.at(best, flat, depth)
        keep |= depth <= best[flat] + DEPTH_BAND

    return keep


def keep_exterior(tris, voxel):
    """Public entry point. voxel doubles as the image-plane cell."""
    if len(tris) == 0:
        return tris
    keep = visible_surface(tris, voxel)
    if not keep.any():
        return tris
    return tris[keep]


def read_stl(path):
    with open(path, "rb") as f:
        head = f.read(84)
        n = struct.unpack("<I", head[80:84])[0]
        raw = np.frombuffer(f.read(n * 50), dtype=np.uint8)
    n = min(n, raw.size // 50)
    rec = raw[: n * 50].reshape(n, 50)
    v = np.frombuffer(rec[:, 12:48].tobytes(), dtype="<f4").reshape(n, 3, 3)
    return v.astype(np.float64)


def cluster(tris, cell):
    """Vertex-cluster decimation at a given cell size.

    Welds every vertex that lands in the same grid cell, drops triangles
    that collapse to a degenerate, and then drops DUPLICATE triangles.
    The dedup is load-bearing, not tidiness: as the cell grows, distinct
    source triangles map onto the same triple of cells, so without it the
    face count plateaus on copies while the vertex count keeps falling.
    A budget search reading that plateau never converges and walks the
    cell size up until the part is a box.
    """
    flat = tris.reshape(-1, 3)
    keys = np.floor(flat / cell).astype(np.int64)
    _, inv = np.unique(keys, axis=0, return_inverse=True)
    inv = np.asarray(inv).ravel()
    # representative position = mean of the cell's members, which keeps the
    # surface smooth rather than snapping it to the lattice
    order = np.argsort(inv)
    sorted_inv = inv[order]
    bounds = np.searchsorted(sorted_inv, np.arange(sorted_inv[-1] + 2))
    sums = np.add.reduceat(flat[order], bounds[:-1], axis=0)
    counts = np.diff(bounds).reshape(-1, 1)
    reps = sums / np.maximum(counts, 1)
    faces = inv.reshape(-1, 3)
    ok = (faces[:, 0] != faces[:, 1]) & (faces[:, 1] != faces[:, 2]) & (faces[:, 0] != faces[:, 2])
    faces = faces[ok]
    if len(faces):
        # Dedup on the SORTED index triple so the two winding orders of one
        # surface triangle count as one, keeping the first occurrence's own
        # winding rather than a re-sorted one.
        _, first = np.unique(np.sort(faces, axis=1), axis=0, return_index=True)
        faces = faces[np.sort(first)]
    return reps, faces


def decimate(tris, budget):
    """Pick the FINEST cell size whose output still fits the budget.

    Bisection on log cell size rather than a grow-until-it-fits walk: the
    walk lands wherever its step happens to cross the budget, which on a
    small part means throwing away most of the detail it was allowed to
    keep. Bisection lands just under the budget every time.
    """
    pts = tris.reshape(-1, 3)
    diag = float(np.linalg.norm(np.max(pts, axis=0) - np.min(pts, axis=0)))
    if diag <= 0:
        return cluster(tris, 1.0)

    # A bisection runs cluster() up to eighteen times, and the two largest
    # parts here are ~11.8 million triangles each, so bisecting on the raw
    # mesh would weld 35 million vertex rows per probe. One cheap pass at a
    # cell far finer than any budget will survive knocks the input down
    # first. It cannot change the answer: every later, coarser cell welds a
    # superset of what this one already welded.
    if len(tris) > 400_000:
        v0, f0 = cluster(tris, diag / 900.0)
        if len(f0):
            tris = v0[f0]

    fine, coarse = diag / 2000.0, diag / 4.0
    verts, faces = cluster(tris, fine)
    if len(faces) <= budget:
        return verts, faces                      # already fits at full detail

    best = cluster(tris, coarse)
    for _ in range(18):
        mid = float(np.sqrt(fine * coarse))
        v, f = cluster(tris, mid)
        if len(f) <= budget:
            best, coarse = (v, f), mid
        else:
            fine = mid
        if coarse / fine < 1.02:
            break
    return best


def write_obj(path, verts, faces, name):
    """Write the part in PadForge's model space.

    Valve's CAD is X = width, Y = height (up positive), Z = depth (front
    positive). Read off the assembly rather than assumed: the bumper sits
    at Y 22 to 41 and the trigger caps at Z -31 to -6, which is the top
    and the back of a pad held normally.

    PadForge is X = width, Y = depth with NEGATIVE toward the camera, and
    Z = height. Confirmed against the shipped Xbox 360 set, where the A
    button lands at Y -19 to -2 and the left bumper at Z 38 to 47.

    So Y = -Z and Z = Y, which is a rotation about X rather than a
    mirror. Winding is therefore LEFT ALONE. An earlier pass swapped the
    two axes without the sign, which is a reflection, and needed the
    winding reversed to compensate; it also put the whole controller
    back to front.
    """
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
    jobs = {}

    # The body is four separate CAD shells (top, front, bottom, battery
    # door) that nest inside each other. They are kept as a LIST and
    # decimated one at a time, then concatenated. Merging first and
    # decimating once welds the inner shell into the outer wherever the
    # cell spans the wall thickness, which turned the whole case into
    # faceted noise with the interior punching through.
    jobs["MainBody.obj"] = [read_stl(os.path.join(SRC, f)) for f in MERGE_BODY]

    # The bumper is one part spanning both sides; split it on the centreline
    # so LeftShoulder and RightShoulder are separately highlightable.
    bump = read_stl(os.path.join(SRC, "BumperGPrime.rev01.01.stl"))
    cx = bump.reshape(-1, 3, 3)[:, :, 0].mean(axis=1)
    jobs["L1.obj"] = bump[cx < 0]
    jobs["R1.obj"] = bump[cx >= 0]

    for src, dst in MAP.items():
        if dst is None:
            continue
        jobs[dst] = read_stl(os.path.join(SRC, src))

    # Every job is a list of shells. One entry for a single solid, four
    # for the case.
    jobs = {k: (v if isinstance(v, list) else [v]) for k, v in jobs.items()}

    # Centre X across the whole assembly so the model sits on the view axis.
    allx = np.concatenate([t.reshape(-1, 3)[:, 0]
                           for shells in jobs.values() for t in shells])
    xmid = (allx.min() + allx.max()) / 2.0

    # Pass one: thin every shell at a cell far below the ~2 mm wall, so
    # nothing welds across it. This is only to make the visibility pass
    # affordable; the raw set is 29.7 million triangles.
    flat = []            # (part name, shell index, triangles)
    raw_total = 0
    for name, shells in sorted(jobs.items()):
        for si, tris in enumerate(shells):
            tris = tris.copy()
            tris[:, :, 0] -= xmid
            raw_total += len(tris)
            v, f = cluster(tris, PREPASS_CELL)
            flat.append((name, si, v[f] if len(f) else tris))
    thinned = sum(len(t) for _, _, t in flat)
    print(f"  prepass at {PREPASS_CELL} mm: {raw_total:,} -> {thinned:,} triangles")

    # Pass two: ONE visibility pass over the assembled controller, so each
    # part occludes the others and the mouldings' interiors drop out. See
    # exterior.py for why this cannot be done part by part.
    allt = np.concatenate([t for _, _, t in flat])
    owner = np.concatenate([np.full(len(t), i) for i, (_, _, t) in enumerate(flat)])
    vis = visible_surface(allt, EXTERIOR_CELL)
    print(f"  visible skin: {int(vis.sum()):,} of {len(allt):,} "
          f"({vis.sum() * 100.0 / len(allt):.1f}%)")

    # Pass three: decimate each part to its budget, now that there is no
    # interior left behind the wall for a coarse cell to collapse onto.
    parts = {}
    for i, (name, si, _) in enumerate(flat):
        parts.setdefault(name, []).append(allt[(owner == i) & vis])

    total_in = total_out = 0
    for name, shells in sorted(parts.items()):
        budget = BUDGET.get(name, DEFAULT_BUDGET)
        sizes = np.array([max(len(t), 1) for t in shells], float)
        share = sizes / sizes.sum()
        allv, allf, base, nin = [], [], 0, 0
        for tris, frac in zip(shells, share):
            if not len(tris):
                continue
            v, f = decimate(tris, max(120, int(budget * frac)))
            allv.append(v)
            allf.append(f + base)
            base += len(v)
            nin += len(tris)
        verts = np.concatenate(allv)
        faces = np.concatenate(allf)
        write_obj(os.path.join(DST, name), verts, faces, name)
        total_in += nin
        total_out += len(faces)
        print(f"  {name:28s} {nin:9,} -> {len(faces):7,} tris  "
              f"({len(verts):,} verts, {len(shells)} shell(s))")
    print(f"TOTAL {total_in:,} -> {total_out:,} triangles")


if __name__ == "__main__":
    main()
