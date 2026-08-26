"""Convert Valve's 2015 Steam Controller CAD into PadForge's per-part OBJs.

Source: Valve's March 2016 "SteamControllerWorkshop03" archive, the STEP
file in it, CC BY-NC-SA 4.0. It is a 50-solid assembly in millimetres
with every part named, and it is the ONLY source worth using for this
pad. The reasons are the whole design:

The archive also ships per-part STLs, and every attempt to decimate them
failed in a way that could not be tuned out. A CAD tessellation at
0.01 mm is 29.7 million triangles; vertex clustering welds the outer wall
to the ribs behind it; quadric collapse stalls on 43,000 unstitched
tessellation seams per case half, and splitting those seams shreds every
thin rim. Measured and rendered, all of it, in the session that wrote
this file.

A STEP file is the exact B-rep surface. Meshing it AT the density the
preview wants, in one pass, gives a manifold mesh with true edges and
nothing to decimate. Load takes 30 s, meshing all 50 solids takes 3 s.

Usage:
    pip install cadquery numpy
    set SC2015_STEP=<path to MM06181.rev01.01.SteamControllerWorkshop03.stp>
    python tools/steam_controller_2015_mesh.py
"""
import io
import os
import sys
import time
import numpy as np

from OCP.STEPCAFControl import STEPCAFControl_Reader
from OCP.TDocStd import TDocStd_Document
from OCP.TCollection import TCollection_ExtendedString
from OCP.XCAFDoc import XCAFDoc_DocumentTool
from OCP.TDF import TDF_LabelSequence, TDF_Label
from OCP.TDataStd import TDataStd_Name
from OCP.BRepMesh import BRepMesh_IncrementalMesh
from OCP.BRepBuilderAPI import BRepBuilderAPI_Copy
from OCP.TopExp import TopExp_Explorer
from OCP.TopAbs import TopAbs_FACE, TopAbs_REVERSED
from OCP.BRep import BRep_Tool
from OCP.TopLoc import TopLoc_Location
from OCP.TopoDS import TopoDS
from OCP.IFSelect import IFSelect_RetDone

STEP = os.environ.get(
    "SC2015_STEP",
    r"C:/tmp/sc2015step/MM06181.rev01.01.SteamControllerWorkshop03.stp")
DST = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "PadForge.App", "3DModels", "SteamController")

# STEP solid name -> (PadForge part file, deflection mm). Several solids
# fold into one file: the four case shells are MainBody, the stick cap
# is a knurled grip on a base, the Steam button is a plastic body with a
# label solid. Deflection is per part so a 9 mm cap is meshed finer than
# a 160 mm case; it is a chord-error tolerance and never moves a vertex
# off the true surface, so a coarser value only means bigger facets,
# never a rounded edge.
PARTS = {
    "CaseTopGPrime":               ("MainBody.obj", 1.0),
    "CaseFrontGPrime":             ("MainBody.obj", 1.0),
    "CaseBottomGPrime":            ("MainBody.obj", 1.0),
    "BatteryDoorMkVI":             ("MainBody.obj", 1.0),
    "BumperGPrime":                ("BUMPER", 0.8),        # split L/R below
    "TriggerCapLeftJAG":           ("Shoulder-Left-Trigger.obj", 0.6),
    "TriggerCapRightJAG":          ("Shoulder-Right-Trigger.obj", 0.6),
    "TrackPadCoverDirectional.01": ("LeftPadTouch.obj", 0.5),
    "TrackPadCoverSmooth.01":      ("RightPadTouch.obj", 0.5),
    "ThumbTopGrip.01":             ("LeftStickClick.obj", 0.5),
    "ThumbTopBase.01":             ("LeftStickClick.obj", 0.5),
    "BatteryLeverLeft":            ("LeftGrip.obj", 0.6),
    "BatteryLeverRight":           ("RightGrip.obj", 0.6),
    "ButtonA-Shot2":               ("B1.obj", 0.25),
    "ButtonB-Shot21":              ("B2.obj", 0.25),
    "ButtonX-Shot2":               ("B3.obj", 0.25),
    "ButtonY-Shot2":               ("B4.obj", 0.25),
    "ButtonStart-Shot2":           ("Start.obj", 0.25),
    "ButtonSelect-Shot2":          ("Back.obj", 0.25),
    "SteamButton_Plastic":         ("Special.obj", 0.25),
    # Shot1 is the printed glyph on each cap, a separate solid in the
    # two-shot mould. Written as its own file so the model class can
    # give it the printed colour and ride it on the cap's highlight.
    "ButtonA-Shot1":               ("B1-Symbol.obj", 0.2),
    "ButtonB-Shot1":               ("B2-Symbol.obj", 0.2),
    "ButtonX-Shot1":               ("B3-Symbol.obj", 0.2),
    "ButtonY-Shot1":               ("B4-Symbol.obj", 0.2),
    "ButtonStart-Shot1":           ("StartIcon.obj", 0.2),
    "ButtonSelect-Shot1":          ("BackIcon.obj", 0.2),
    "SteamButton_Label":           ("SpecialIcon.obj", 0.2),
}
# Everything else in the assembly (PCB, membrane, snap domes, contacts,
# USB socket, shield, pucks, slider, batteries) is inside the case and
# is dropped.

ANG_DEFL = 0.6      # rad, facet-to-facet angle bound
CREASE_DEG = 40.0


def label_name(lbl):
    n = TDataStd_Name()
    return n.Get().ToExtString() if lbl.FindAttribute(TDataStd_Name.GetID_s(), n) else ""


def load_leaves(path):
    """Every leaf solid in the assembly, with its name and world placement."""
    doc = TDocStd_Document(TCollection_ExtendedString("doc"))
    rdr = STEPCAFControl_Reader()
    rdr.SetNameMode(True)
    if rdr.ReadFile(path) != IFSelect_RetDone:
        raise SystemExit(f"cannot read {path}")
    rdr.Transfer(doc)
    st = XCAFDoc_DocumentTool.ShapeTool_s(doc.Main())
    free = TDF_LabelSequence()
    st.GetFreeShapes(free)
    leaves = []

    def walk(lbl, loc):
        if st.IsAssembly_s(lbl):
            kids = TDF_LabelSequence()
            st.GetComponents_s(lbl, kids)
            for i in range(1, kids.Length() + 1):
                k = kids.Value(i)
                ref = TDF_Label()
                sub = st.GetLocation_s(k)
                walk(ref if st.GetReferredShape_s(k, ref) else k, loc.Multiplied(sub))
        else:
            leaves.append((label_name(lbl), st.GetShape_s(lbl).Moved(loc)))

    for i in range(1, free.Length() + 1):
        walk(free.Value(i), TopLoc_Location())
    return leaves


def tessellate(shape, lin_defl):
    """Mesh a solid straight from its B-rep. Returns (verts, faces) with
    outward winding, using each face's own orientation flag."""
    s = BRepBuilderAPI_Copy(shape).Shape()
    BRepMesh_IncrementalMesh(s, lin_defl, False, ANG_DEFL, True)
    verts, faces = [], []
    base = 0
    ex = TopExp_Explorer(s, TopAbs_FACE)
    while ex.More():
        face = TopoDS.Face_s(ex.Current())
        loc = TopLoc_Location()
        tri = BRep_Tool.Triangulation_s(face, loc)
        if tri is not None:
            trsf = loc.Transformation()
            n = tri.NbNodes()
            for i in range(1, n + 1):
                p = tri.Node(i).Transformed(trsf)
                verts.append((p.X(), p.Y(), p.Z()))
            rev = face.Orientation() == TopAbs_REVERSED
            for i in range(1, tri.NbTriangles() + 1):
                a, b, c = tri.Triangle(i).Get()
                if rev:
                    b, c = c, b
                faces.append((base + a - 1, base + b - 1, base + c - 1))
            base += n
        ex.Next()
    return np.array(verts, np.float64), np.array(faces, np.int64)


def weld(verts, faces, tol=1e-4):
    """Merge coincident vertices across face boundaries so normals can
    average across them. Tolerance is far below any feature."""
    q = np.round(verts / tol).astype(np.int64)
    uniq, inv = np.unique(q, axis=0, return_inverse=True)
    inv = np.asarray(inv).ravel()
    first = np.zeros(len(uniq), np.int64)
    first[inv[::-1]] = np.arange(len(inv))[::-1]
    v = verts[first]
    f = inv[faces]
    ok = (f[:, 0] != f[:, 1]) & (f[:, 1] != f[:, 2]) & (f[:, 0] != f[:, 2])
    return v, f[ok]


def crease_normals(verts, faces, crease_deg=CREASE_DEG):
    """Per-corner normals averaged only within the crease angle, so a
    vertex on a moulded edge keeps a different normal for each side.
    Without explicit normals a viewport averages across every face at a
    vertex and rounds every edge; that was the 'clay' look."""
    fn = np.cross(verts[faces[:, 1]] - verts[faces[:, 0]],
                  verts[faces[:, 2]] - verts[faces[:, 0]])
    ln = np.linalg.norm(fn, axis=1, keepdims=True)
    fn = fn / np.maximum(ln, 1e-20)
    area = ln[:, 0]
    cos_lim = np.cos(np.radians(crease_deg))
    cv = faces.ravel()
    cf = np.repeat(np.arange(len(faces)), 3)
    order = np.argsort(cv, kind="stable")
    cv, cf = cv[order], cf[order]
    starts = np.searchsorted(cv, np.arange(len(verts) + 1))
    normals = []
    corner = np.zeros((len(faces), 3), np.int64)
    for v in range(len(verts)):
        a, b = starts[v], starts[v + 1]
        if a == b:
            continue
        groups = []
        for f in cf[a:b]:
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
                corner[f, np.nonzero(faces[f] == v)[0][0]] = k
    return np.array(normals), corner


def write_obj(path, verts, faces, name):
    """Valve CAD is X width, Y height up, Z depth front. PadForge is X
    width, Y depth with the front NEGATIVE, Z height. Y = -Z and Z = Y is
    a rotation about X, not a mirror, so the winding is left alone."""
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
    t0 = time.time()
    leaves = load_leaves(STEP)
    print(f"  loaded {len(leaves)} solids in {time.time() - t0:.0f}s")

    meshes = {}                     # file -> list of (verts, faces)
    for name, shape in leaves:
        if name not in PARTS:
            continue
        target, defl = PARTS[name]
        v, f = tessellate(shape, defl)
        if target == "BUMPER":
            # One solid spanning both sides; split on the centreline so
            # each shoulder is its own highlight target.
            cx = v[f][:, :, 0].mean(axis=1)
            for side, sel in (("L1.obj", cx < 0), ("R1.obj", cx >= 0)):
                meshes.setdefault(side, []).append((v, f[sel]))
        else:
            meshes.setdefault(target, []).append((v, f))

    # Centre X on the assembly so the model sits on the camera axis.
    allx = np.concatenate([v[:, 0] for parts in meshes.values() for v, _ in parts])
    xmid = (allx.min() + allx.max()) / 2.0

    total = 0
    for fname, parts in sorted(meshes.items()):
        av, af, base = [], [], 0
        for v, f in parts:
            av.append(v)
            af.append(f + base)
            base += len(v)
        v = np.concatenate(av)
        f = np.concatenate(af)
        v = v.copy()
        v[:, 0] -= xmid
        v, f = weld(v, f)
        write_obj(os.path.join(DST, fname), v, f, fname)
        total += len(f)
        print(f"  {fname:26s} {len(f):7,} tris  ({len(v):,} verts)")
    print(f"TOTAL {total:,} triangles in {time.time() - t0:.0f}s")


if __name__ == "__main__":
    main()
