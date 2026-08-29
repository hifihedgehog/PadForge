"""Give the 2026 Steam Controller's trackpad meshes only their own surface.

`LeftPadTouch.obj` and `RightPadTouch.obj` shipped as grab bags. Split
into connected components, each one holds three different things:

  * the pad FACE, one component whose footprint covers the whole pad,
  * the pad's surround, four corner blocks and a rim, and
  * fragments sitting on the REAR PADDLES, 737 vertices over R4 and R5
    and 169 over L4 and L5, more than 20 mm behind the front face.

The rear fragments are why hovering a trackpad lit part of a rear paddle
and why a paddle never lit whole: those pieces belong to the pad's group,
so they took the pad's highlight and sat out the paddle's. They move to
the paddle they overlap.

The face comes out on its own as `{side}PadFace.obj` so the finger dot
has the touch surface to ride rather than the whole assembly's bounding
plane, which the corner blocks and the rim push out past the pad's edge.

Idempotent: rerunning finds nothing to move and says so.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
MODELS = os.path.join(os.path.dirname(HERE), "PadForge.App", "3DModels", "SteamController2")


def load(path):
    """Positions, normals, texcoords, and faces as (v, vt, vn) triples."""
    v, vt, vn, faces, head = [], [], [], [], []
    for line in open(path, encoding="utf-8", errors="ignore"):
        if line.startswith("v "):
            v.append(tuple(float(x) for x in line.split()[1:4]))
        elif line.startswith("vt "):
            vt.append(line.split()[1:])
        elif line.startswith("vn "):
            vn.append(tuple(float(x) for x in line.split()[1:4]))
        elif line.startswith("f "):
            corners = []
            for tok in line.split()[1:]:
                bits = (tok.split("/") + ["", ""])[:3]
                corners.append(tuple(int(b) - 1 if b else -1 for b in bits))
            for k in range(1, len(corners) - 1):
                faces.append((corners[0], corners[k], corners[k + 1]))
        elif not line.startswith(("f ", "v ", "vt ", "vn ")):
            head.append(line.rstrip("\n"))
    return v, vt, vn, faces


def components(vcount, faces):
    parent = list(range(vcount))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    for tri in faces:
        a, b, c = (t[0] for t in tri)
        for x, y in ((a, b), (b, c)):
            rx, ry = find(x), find(y)
            if rx != ry:
                parent[rx] = ry

    groups = {}
    for i, tri in enumerate(faces):
        groups.setdefault(find(tri[0][0]), []).append(i)
    return list(groups.values())


def bounds(v, vt, vn, faces, idxs):
    lo = [1e30] * 3
    hi = [-1e30] * 3
    for i in idxs:
        for corner in faces[i]:
            p = v[corner[0]]
            for k in range(3):
                lo[k] = min(lo[k], p[k])
                hi[k] = max(hi[k], p[k])
    return lo, hi


def overlaps(a, b):
    (alo, ahi), (blo, bhi) = a, b
    return all(alo[k] <= bhi[k] and ahi[k] >= blo[k] for k in range(3))


def write(path, v, vt, vn, faces, idxs, note):
    """Writes the given faces with their vertices renumbered."""
    vmap, tmap, nmap = {}, {}, {}
    ov, ot, on, of = [], [], [], []
    for i in idxs:
        tri = []
        for (vi, ti, ni) in faces[i]:
            if vi not in vmap:
                vmap[vi] = len(ov) + 1
                ov.append(v[vi])
            if ti >= 0 and ti not in tmap:
                tmap[ti] = len(ot) + 1
                ot.append(vt[ti])
            if ni >= 0 and ni not in nmap:
                nmap[ni] = len(on) + 1
                on.append(vn[ni])
            tri.append((vmap[vi], tmap.get(ti), nmap.get(ni)))
        of.append(tri)

    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(f"# {note}\n")
        for p in ov:
            fh.write("v %.6f %.6f %.6f\n" % p)
        for t in ot:
            fh.write("vt " + " ".join(t) + "\n")
        for n in on:
            fh.write("vn %.6f %.6f %.6f\n" % n)
        for tri in of:
            parts = []
            for (vi, ti, ni) in tri:
                if ni and ti:
                    parts.append(f"{vi}/{ti}/{ni}")
                elif ni:
                    parts.append(f"{vi}//{ni}")
                elif ti:
                    parts.append(f"{vi}/{ti}")
                else:
                    parts.append(str(vi))
            fh.write("f " + " ".join(parts) + "\n")
    print(f"   {os.path.basename(path)}: {len(ov)} verts, {len(of)} tris")


def append(path, v, vt, vn, faces, idxs, note):
    """Appends faces to an existing OBJ, renumbering onto its tail."""
    ev, evt, evn, _ = load(path)
    base_v, base_t, base_n = len(ev), len(evt), len(evn)

    vmap, tmap, nmap = {}, {}, {}
    lines = []
    for i in idxs:
        tri = []
        for (vi, ti, ni) in faces[i]:
            if vi not in vmap:
                vmap[vi] = base_v + len(vmap) + 1
                lines.append("v %.6f %.6f %.6f" % v[vi])
            if ti >= 0 and ti not in tmap:
                tmap[ti] = base_t + len(tmap) + 1
            if ni >= 0 and ni not in nmap:
                nmap[ni] = base_n + len(nmap) + 1
            tri.append((vmap[vi], tmap.get(ti), nmap.get(ni)))
        tri_lines = []
        for (a, b, c) in [tri]:
            pass
        lines.append(("f " + " ".join(
            f"{x}/{y}/{z}" if (y and z) else (f"{x}//{z}" if z else (f"{x}/{y}" if y else str(x)))
            for (x, y, z) in tri)))

    # texcoords and normals in the order their maps assigned them
    tex = [None] * len(tmap)
    for ti, slot in tmap.items():
        tex[slot - base_t - 1] = vt[ti]
    nor = [None] * len(nmap)
    for ni, slot in nmap.items():
        nor[slot - base_n - 1] = vn[ni]

    with open(path, "a", encoding="utf-8", newline="\n") as fh:
        fh.write(f"# {note}\n")
        for ln in lines:
            if ln.startswith("v "):
                fh.write(ln + "\n")
        for t in tex:
            fh.write("vt " + " ".join(t) + "\n")
        for n in nor:
            fh.write("vn %.6f %.6f %.6f\n" % n)
        for ln in lines:
            if ln.startswith("f "):
                fh.write(ln + "\n")
    print(f"   {os.path.basename(path)}: +{len(vmap)} verts, +{sum(1 for l in lines if l.startswith('f '))} tris")


def main():
    paddle_bounds = {}
    for name in ("R4", "R5", "L4", "L5"):
        v, vt, vn, faces = load(os.path.join(MODELS, f"{name}.obj"))
        paddle_bounds[name] = bounds(v, vt, vn, faces, range(len(faces)))

    for side, paddles in (("Left", ("L4", "L5")), ("Right", ("R4", "R5"))):
        src = os.path.join(MODELS, f"{side}PadTouch.obj")
        face_out = os.path.join(MODELS, f"{side}PadFace.obj")
        if os.path.exists(face_out):
            print(f"{side}: already split")
            continue

        v, vt, vn, faces = load(src)
        comps = components(len(v), faces)
        print(f"{side}PadTouch.obj: {len(faces)} tris in {len(comps)} components")

        rear = {p: [] for p in paddles}
        front = []
        for idxs in comps:
            lo, hi = bounds(v, vt, vn, faces, idxs)
            if lo[1] > 0.0:                       # behind the mid plane
                for p in paddles:
                    if overlaps((lo, hi), paddle_bounds[p]):
                        rear[p].extend(idxs)
                        break
                else:
                    front.extend(idxs)            # rear, but on no paddle
            else:
                front.append(idxs) if False else front.extend(idxs)

        # The face is the front component with the widest footprint: it is
        # the only one that spans the pad, the rest being corner blocks.
        best, best_area = None, -1.0
        for idxs in comps:
            if not set(idxs) <= set(front):
                continue
            lo, hi = bounds(v, vt, vn, faces, idxs)
            area = (hi[0] - lo[0]) * (hi[2] - lo[2])
            if area > best_area:
                best, best_area = idxs, area
        face = set(best or [])
        surround = [i for i in front if i not in face]

        write(face_out, v, vt, vn, faces, sorted(face),
              f"The {side.lower()} trackpad's touch face, cut from {side}PadTouch.obj "
              f"by tools/steam_controller_2026_pads.py")
        write(src, v, vt, vn, faces, sorted(surround),
              f"The {side.lower()} trackpad's surround. Its face is {side}PadFace.obj and its "
              f"fragments on the rear paddles moved to them, both by "
              f"tools/steam_controller_2026_pads.py")
        for p, idxs in rear.items():
            if not idxs:
                continue
            append(os.path.join(MODELS, f"{p}.obj"), v, vt, vn, faces, sorted(idxs),
                   f"Moved off {side}PadTouch.obj by tools/steam_controller_2026_pads.py: "
                   f"this sits on {p} and was taking the trackpad's highlight")


def move_lettering():
    """Move each paddle's engraved lettering out of the shell.

    MainBody.obj carries the glyph strokes for the bottom paddles, dozens
    of components under 3 mm sitting wholly inside R5's and L5's bounds.
    They stayed shell-colored while the paddle lit, which is the lettering
    that never highlighted. The shell's own geometry around a paddle
    straddles its bounds rather than sitting inside them, so "fully
    inside" separates the two without a size rule doing the work.

    Idempotent: rerunning finds nothing inside and says so.
    """
    body = os.path.join(MODELS, "MainBody.obj")
    v, vt, vn, faces = load(body)

    paddle_bounds = {}
    for name in ("R4", "R5", "L4", "L5"):
        pv, pvt, pvn, pf = load(os.path.join(MODELS, f"{name}.obj"))
        paddle_bounds[name] = bounds(pv, pvt, pvn, pf, range(len(pf)))

    moved = {p: [] for p in paddle_bounds}
    kept = []
    for idxs in components(len(v), faces):
        lo, hi = bounds(v, vt, vn, faces, idxs)
        for name, (plo, phi) in paddle_bounds.items():
            inside = all(lo[k] >= plo[k] - 0.6 and hi[k] <= phi[k] + 0.6 for k in range(3))
            if inside and max(hi[k] - lo[k] for k in range(3)) < 4.0:
                moved[name].extend(idxs)
                break
        else:
            kept.extend(idxs)

    total = sum(len(x) for x in moved.values())
    if total == 0:
        print("MainBody.obj: no lettering left inside a paddle")
        return

    print(f"MainBody.obj: {len(faces)} tris, moving {total} onto the paddles")
    write(body, v, vt, vn, faces, sorted(kept),
          "The 2026 shell. Its paddle lettering moved onto the paddles by "
          "tools/steam_controller_2026_pads.py")
    for name, idxs in moved.items():
        if not idxs:
            continue
        append(os.path.join(MODELS, f"{name}.obj"), v, vt, vn, faces, sorted(idxs),
               f"{name}'s lettering, moved off MainBody.obj by "
               f"tools/steam_controller_2026_pads.py: it sat inside the paddle and stayed "
               f"shell-colored while the paddle lit")


if __name__ == "__main__":
    main()
    move_lettering()
