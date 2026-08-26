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
from OCP.IMeshTools import IMeshTools_Parameters
from OCP.BRepLib import BRepLib_ToolTriangulatedShape
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

# STEP solid name -> (PadForge part file, MinSize mm). Several solids fold
# into one file: the four case shells are MainBody, the stick cap is a
# knurled grip on a base, the Steam button is a plastic body with a label
# solid.
#
# Density is set by LIN_DEFL and ANG_DEFL for every part; the per-part
# number is the element-size FLOOR the mesher may not subdivide below.
# That floor is what makes a fine angular bound affordable. Measured: at
# 0.3 mm / 0.15 rad with no floor the case goes to 1.6 million triangles,
# almost all of it on fillets and knurl ridges too small to see. With a
# 1.0 mm floor the same settings give 108k, FEWER than the old coarse
# mesh, while every large curve is bounded at 8.6 degrees per facet
# instead of 34. Glyph solids get a smaller floor because their strokes
# are about a millimetre wide.
PARTS = {
    "CaseTopGPrime":               ("MainBody.obj", 1.0),
    "CaseFrontGPrime":             ("MainBody.obj", 1.0),
    "CaseBottomGPrime":            ("MainBody.obj", 1.0),
    "BatteryDoorMkVI":             ("COVER", 1.0),          # wings split off below
    "BumperGPrime":                ("BUMPER", 1.0),        # split L/R below
    "TriggerCapLeftJAG":           ("Shoulder-Left-Trigger.obj", 1.0),
    "TriggerCapRightJAG":          ("Shoulder-Right-Trigger.obj", 1.0),
    "TrackPadCoverDirectional.01": ("LeftPadTouch.obj", 1.0),
    "TrackPadCoverSmooth.01":      ("RightPadTouch.obj", 1.0),
    # The stick is two solids and they are two DIFFERENT controls: the
    # knurled cap top (18.6 mm) is the direction surface every other model
    # calls its ring, and the base under it (26.4 mm) is the click. Folding
    # both into the click mesh left this pad with no ring group, so its
    # stick had no direction target and no visible collar at all.
    "ThumbTopGrip.01":             ("Joystick-Left-Ring.obj", 1.0),
    "ThumbTopBase.01":             ("LeftStickClick.obj", 1.0),
    # The lever behind each grip paddle. Mostly hidden inside the handle,
    # visible through a slot at the top, and part of the same control, so
    # it rides the grip group with the paddle face split off the cover.
    "BatteryLeverLeft":            ("LeftGrip.obj", 1.0),
    "BatteryLeverRight":           ("RightGrip.obj", 1.0),
    "ButtonA-Shot2":               ("B1.obj", 1.0),
    "ButtonB-Shot21":              ("B2.obj", 1.0),
    "ButtonX-Shot2":               ("B3.obj", 1.0),
    "ButtonY-Shot2":               ("B4.obj", 1.0),
    "ButtonStart-Shot2":           ("Start.obj", 1.0),
    "ButtonSelect-Shot2":          ("Back.obj", 1.0),
    "SteamButton_Plastic":         ("Special.obj", 1.0),
    # Shot1 is the printed glyph on each cap, a separate solid in the
    # two-shot mould. Written as its own file so the model class can
    # give it the printed colour and ride it on the cap's highlight.
    "ButtonA-Shot1":               ("B1-Symbol.obj", 0.4),
    "ButtonB-Shot1":               ("B2-Symbol.obj", 0.4),
    "ButtonX-Shot1":               ("B3-Symbol.obj", 0.4),
    "ButtonY-Shot1":               ("B4-Symbol.obj", 0.4),
    "ButtonStart-Shot1":           ("StartIcon.obj", 0.4),
    "ButtonSelect-Shot1":          ("BackIcon.obj", 0.4),
    "SteamButton_Label":           ("SpecialIcon.obj", 0.4),
}
# Everything else in the assembly (PCB, membrane, snap domes, contacts,
# USB socket, shield, pucks, slider, batteries) is inside the case and
# is dropped.

LIN_DEFL = 0.3      # mm, chord error
ANG_DEFL = 0.15     # rad, facet-to-facet bound: 8.6 degrees


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


def tessellate(shape, min_size):
    """Mesh a solid straight from its B-rep. Returns verts, per-node
    SURFACE normals, and faces with outward winding.

    Normals come from the B-rep surface at each node, never from the
    triangles. Geometry-derived normals need a crease threshold, and on a
    curved grip meshed at 34 degrees some facet pairs crossed it and some
    did not, which is exactly the random hard edges a viewer saw on the
    handles. A surface normal is exact: smooth where the surface is
    smooth, different across a boundary only where two faces really meet
    at an angle. Nodes are per B-rep face, so that boundary carries one
    node per side with each side's own normal, and BRepMesh discretises
    the shared edge identically on both, so no crack opens.
    """
    s = BRepBuilderAPI_Copy(shape).Shape()
    prm = IMeshTools_Parameters()
    prm.Deflection = LIN_DEFL
    prm.Angle = ANG_DEFL
    prm.MinSize = min_size
    prm.Relative = False
    prm.InParallel = True
    prm.AllowQualityDecrease = True
    BRepMesh_IncrementalMesh(s, prm)
    verts, norms, faces = [], [], []
    base = 0
    ex = TopExp_Explorer(s, TopAbs_FACE)
    while ex.More():
        face = TopoDS.Face_s(ex.Current())
        loc = TopLoc_Location()
        tri = BRep_Tool.Triangulation_s(face, loc)
        if tri is not None:
            BRepLib_ToolTriangulatedShape.ComputeNormals_s(face, tri)
            trsf = loc.Transformation()
            rev = face.Orientation() == TopAbs_REVERSED
            sign = -1.0 if rev else 1.0
            n = tri.NbNodes()
            for i in range(1, n + 1):
                p = tri.Node(i).Transformed(trsf)
                d = tri.Normal(i).Transformed(trsf)
                verts.append((p.X(), p.Y(), p.Z()))
                norms.append((sign * d.X(), sign * d.Y(), sign * d.Z()))
            for i in range(1, tri.NbTriangles() + 1):
                a, b, c = tri.Triangle(i).Get()
                if rev:
                    b, c = c, b
                faces.append((base + a - 1, base + b - 1, base + c - 1))
            base += n
        ex.Next()
    return (np.array(verts, np.float64), np.array(norms, np.float64),
            np.array(faces, np.int64))


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


def compact(verts, norms, faces):
    """Drop vertices no face references, renumbering the rest. The bumper
    halves are split by face from one solid's node array, so each carried
    thousands of nodes the other half owned."""
    used = np.unique(faces.ravel())
    if len(used) == len(verts):
        return verts, norms, faces
    remap = np.full(len(verts), -1, np.int64)
    remap[used] = np.arange(len(used))
    return verts[used], norms[used], remap[faces]


def write_obj(path, verts, norms, faces, name):
    """Valve's CAD is X width, Y height up, Z depth with the FRONT at
    POSITIVE Z. Measured, not assumed: the pad covers, ABXY caps and the
    stick cap all sit at Z +3 to +24.5, the battery door at Z -29 to +1.
    PadForge is X width, Y depth with the front NEGATIVE, Z height. So
    Y = -Z and Z = Y, a rotation about X, not a mirror, and the B-rep
    winding is left alone. Normals take the same map.

    A build once shipped with the sign the other way because an in-app
    screenshot showed the back of the pad. That was the viewport's own
    yaw, which persists across model swaps. A picture is not evidence of
    handedness; the Z sign of a known front part is."""
    out = np.column_stack([verts[:, 0], -verts[:, 2], verts[:, 1]])
    nout = np.column_stack([norms[:, 0], -norms[:, 2], norms[:, 1]])
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("# %s\n# Valve Steam Controller (2015) CAD, CC BY-NC-SA 4.0\n" % name)
        for v in out:
            f.write("v %.4f %.4f %.4f\n" % (v[0], v[1], v[2]))
        for n in nout:
            f.write("vn %.4f %.4f %.4f\n" % (n[0], n[1], n[2]))
        f.write("g %s\n" % os.path.splitext(name)[0])
        for a, b, c in faces + 1:
            f.write("f %d//%d %d//%d %d//%d\n" % (a, a, b, b, c, c))


def main():
    os.makedirs(DST, exist_ok=True)
    t0 = time.time()
    leaves = load_leaves(STEP)
    print(f"  loaded {len(leaves)} solids in {time.time() - t0:.0f}s")

    meshes = {}                     # file -> list of (verts, norms, faces)
    for name, shape in leaves:
        if name not in PARTS:
            continue
        target, min_size = PARTS[name]
        v, nrm, f = tessellate(shape, min_size)
        if target == "COVER":
            # The rear cover is ONE solid shaped like a bat: a central
            # battery-door panel carrying the Valve logo, with a WING
            # flaring out over each handle. Those flares are the grip
            # paddles (owner call), so they split off here the way the
            # bumper solid splits into two shoulders.
            #
            # The crease between panel and wing sits at |x| = 30 mm. That
            # is where the cover stops being flat and starts wrapping the
            # handle: binned every 5 mm, its depth runs 8.2 mm just
            # inboard of the crease and 11.2, 18.5 then 27.3 mm outboard.
            # Rendering the cut against the moulded crease line puts it on
            # top of it.
            cx = v[f][:, :, 0].mean(axis=1)
            meshes.setdefault("LeftGrip.obj", []).append((v, nrm, f[cx < -30.0]))
            meshes.setdefault("RightGrip.obj", []).append((v, nrm, f[cx > 30.0]))
            meshes.setdefault("MainBody.obj", []).append(
                (v, nrm, f[(cx >= -30.0) & (cx <= 30.0)]))
        elif target == "BUMPER":
            # One solid spanning both sides; split on the centreline so
            # each shoulder is its own highlight target.
            cx = v[f][:, :, 0].mean(axis=1)
            for side, sel in (("L1.obj", cx < 0), ("R1.obj", cx >= 0)):
                meshes.setdefault(side, []).append((v, nrm, f[sel]))
        else:
            meshes.setdefault(target, []).append((v, nrm, f))

    # Centre X on the assembly so the model sits on the camera axis.
    allx = np.concatenate([v[:, 0] for parts in meshes.values() for v, _, _ in parts])
    xmid = (allx.min() + allx.max()) / 2.0

    total = 0
    for fname, parts in sorted(meshes.items()):
        av, an, af, base = [], [], [], 0
        for v, nrm, f in parts:
            av.append(v)
            an.append(nrm)
            af.append(f + base)
            base += len(v)
        v = np.concatenate(av)
        nrm = np.concatenate(an)
        f = np.concatenate(af)
        v = v.copy()
        v[:, 0] -= xmid
        # No weld: nodes are per B-rep face on purpose, each with its own
        # surface normal, and merging them would average across creases.
        v, nrm, f = compact(v, nrm, f)
        write_obj(os.path.join(DST, fname), v, nrm, f, fname)
        total += len(f)
        print(f"  {fname:26s} {len(f):7,} tris  ({len(v):,} verts)")
    print(f"TOTAL {total:,} triangles in {time.time() - t0:.0f}s")


if __name__ == "__main__":
    main()
