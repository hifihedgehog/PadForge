"""2D colorways from the Gamepad-Asset-Pack, for the five stock families
plus the derived DualSense Edge set.

WHAT A COLORWAY NEEDS. The 2D canvas shows exactly three kinds of art at
rest: the base render, the two TriggerBase silhouettes, and the two
StickRing sprites. Everything else (buttons, click rings, trigger fill)
is press-highlight art in the pack's shared cyan treatment and stays
colorway-neutral. So a colorway is one base plus at most four rest
sprites, and only the sprites that actually differ from the default's
are emitted (a black stick stays one shared file across colorways).

WHERE THE ART COMES FROM. Every colorway folder in the pack ships a
prepared ``*_base*.png`` on the same canvas as the app's stock base (the
stock bases ARE those files for the default colorways, verified by
hash). The rest sprites are cut from the colorway's full template
render: crop at the layout table's own bbox, alpha from the SHIPPED
default sprite, RGB from the template. Template discovery is by
CONTENT, not filename (several pack filenames are truncated): the full
render is the same-canvas image that differs from the prepared base
inside both stick bboxes.

VALIDATION. For each family the same extraction is run on the DEFAULT
colorway and compared against the shipped sprite pixel-for-pixel over
the opaque region. That is the proof the method cuts the same art the
app already renders, before it is trusted on any other colorway.

OUTPUT. Variant files land beside the stock ones as
``<stem>_<AppearanceId>.png``; the registry is emitted as the generated
``PadForge.App/Models2D/Controller2DColorways.cs``. Appearance ids reuse
the 3D model families' ids wherever the same physical colorway exists
(one persisted selection drives both views); 2D-only colorways add new
ids in the same PascalCase style.
"""
import io
import os
import re
import sys

import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
APP2D = os.path.join(ROOT, "PadForge.App", "2DModels")
LAYOUT_CS = os.path.join(ROOT, "PadForge.App", "Models2D", "ControllerOverlayLayout.cs")
REGISTRY_CS = os.path.join(ROOT, "PadForge.App", "Models2D", "Controller2DColorways.cs")
PACK = os.path.join(os.path.dirname(ROOT), "Gamepad-Asset-Pack", "Controller Asset Pack")

# RMSE (0..255 scale, judged pixels) thresholds. VALIDATE: the
# default-colorway extraction must reproduce the shipped sprite this
# closely, proving the anchor and method before any variant is trusted.
# EMIT: a variant sprite is emitted only when its extraction differs from
# the DEFAULT colorway's extraction by more than this. Comparing
# extraction-to-extraction cancels the method's own noise floor (soft
# edges, template anti-aliasing), which comparing against the shipped
# file cannot: identical black sticks would otherwise re-emit on every
# colorway at the noise floor.
VALIDATE_RMSE = 14.0
EMIT_RMSE = 3.0

# family config: app folder, family key in the persisted appearance store,
# stock base file, pack colorway dirs -> (id, display name), default id
# first. XboxSeries ids/names are the 3D model's own so one selection
# drives both views; DualSense/DS4 reuse the 3D ids where they overlap.
FAMILIES = [
    {
        "folder": "DualSense",
        "family": "DualSense",
        "base": "DualSense_base.png",
        "packdir": "DualSense Controller Image/Default/Templates",
        "colorways": [
            ("White", "White", "White"),
            ("Midnight", "Midnight Black", "Midnight Black"),
            ("CosmicRed", "Cosmic Red", "Cosmic Red"),
            ("GalacticPurple", "Galactic Purple", "Galactic Purple"),
            ("NovaPink", "Nova Pink", "Nova Pink"),
            ("StarlightBlue", "Starlight Blue", "Starlight Blue"),
        ],
    },
    {
        "folder": "DS4",
        "family": "DS4",
        "base": "DS4_V2_base.png",
        "packdir": "DualShock 4 Controller Images/Default Theme/DualShock 4 Templates/DS4 V2",
        "colorways": [
            ("JetBlack", "Jet Black", "Jet Black"),
            ("GlacierWhite", "Glacier White", "Glacier White"),
            ("Gold", "Gold", "Gold"),
            ("MagmaRed", "Magma Red", "Magma Red"),
            ("MidnightBlue", "Midnight Blue", "Midnight Blue"),
        ],
    },
    {
        "folder": "XBOXSERIES",
        "family": "XboxSeries",
        "base": "XBSeries_base.png",
        "packdir": "Xbox Wireless Controller Images/Default Theme/Template/Xbox Series X Controller",
        "colorways": [
            ("Robot", "Robot White", "White"),
            ("Carbon", "Carbon Black", "Black"),
            ("ElectricVolt", "Electric Volt", "Volt"),
            ("ShockBlue", "Shock Blue", "Blue"),
            ("PulseRed", "Pulse Red", "Red"),
            ("DeepPink", "Deep Pink", "Deep Pink"),
        ],
        # The pack ships the per-colorway rest sprites itself (the app's
        # stock triggers ARE its _White files, hash-identical): the stock
        # set is Robot White, every other colorway takes the pack's
        # default black triggers, and four colorways recolor the sticks.
        "pack_sprites_dir": "Xbox Wireless Controller Images/Default Theme/Theme Assets/Xbox Series X Active Presses/Color",
        "pack_sprites": {
            "Carbon": {
                "XBSeries_LeftTrigger.png": "XBSeries_LeftTrigger.png",
                "XBSeries_RightTrigger.png": "XBSeries_RightTrigger.png",
            },
            "ElectricVolt": {
                "XBSeries_LeftTrigger.png": "XBSeries_LeftTrigger.png",
                "XBSeries_RightTrigger.png": "XBSeries_RightTrigger.png",
                "XBSeries_LeftStick.png": "XBSeries_LeftStick_Volt.png",
                "XBSeries_RightStick.png": "XBSeries_RightStick_Volt.png",
            },
            "ShockBlue": {
                "XBSeries_LeftTrigger.png": "XBSeries_LeftTrigger.png",
                "XBSeries_RightTrigger.png": "XBSeries_RightTrigger.png",
                "XBSeries_LeftStick.png": "XBSeries_LeftStick_Blue.png",
                "XBSeries_RightStick.png": "XBSeries_RightStick_Blue.png",
            },
            "PulseRed": {
                "XBSeries_LeftTrigger.png": "XBSeries_LeftTrigger.png",
                "XBSeries_RightTrigger.png": "XBSeries_RightTrigger.png",
                "XBSeries_LeftStick.png": "XBSeries_LeftStick_Red.png",
                "XBSeries_RightStick.png": "XBSeries_RightStick_Red.png",
            },
            "DeepPink": {
                "XBSeries_LeftTrigger.png": "XBSeries_LeftTrigger.png",
                "XBSeries_RightTrigger.png": "XBSeries_RightTrigger.png",
                "XBSeries_LeftStick.png": "XBSeries_LeftStick_Pink.png",
                "XBSeries_RightStick.png": "XBSeries_RightStick_Pink.png",
            },
        },
    },
    {
        "folder": "XBOXONE",
        "family": "XboxOne",
        "base": "XB1_S_base.png",
        "packdir": "Xbox Wireless Controller Images/Default Theme/Template/Xbox One S Controller",
        "colorways": [
            ("White", "White", "White"),
            ("Black", "Black", "Black"),
            ("Blue", "Blue", "Blue"),
            ("Red", "Red", "Red"),
        ],
        # Black shares the stock sprites (the One S wears black sticks and
        # triggers in both shells; the pack ships no Black variants).
        "pack_sprites_dir": "Xbox Wireless Controller Images/Default Theme/Theme Assets/Xbox One Active Presses/Color",
        "pack_sprites": {
            "Black": {},
            "Blue": {
                "XB1_LeftTrigger.png": "XB1_LeftTrigger_Blue.png",
                "XB1_RightTrigger.png": "XB1_RightTrigger_Blue.png",
                "XB1_LeftStick.png": "XB1_LeftStick_Blue.png",
                "XB1_RightStick.png": "XB1_RightStick_Blue.png",
            },
            "Red": {
                "XB1_LeftTrigger.png": "XB1_LeftTrigger_Red.png",
                "XB1_RightTrigger.png": "XB1_RightTrigger_Red.png",
                "XB1_LeftStick.png": "XB1_LeftStick_Red.png",
                "XB1_RightStick.png": "XB1_RightStick_Red.png",
            },
        },
    },
    {
        "folder": "XBOX360",
        "family": "Xbox360",
        "base": "XB360_base.png",
        "packdir": "Xbox 360 Controller Images/Default Theme/Templates",
        "colorways": [
            ("White", "White", "White"),
            ("Black", "Black", "Black"),
        ],
        "pack_sprites_dir": "Xbox 360 Controller Images/Default Theme/Theme SVG/Theme Assets/Active Presses/Button Color",
        "pack_sprites": {
            "Black": {
                "XB360_LeftTrigger.png": "XB360_LeftTrigger_Black.png",
                "XB360_RightTrigger.png": "XB360_RightTrigger_Black.png",
                "XB360_LeftStick.png": "XB360_LeftStick_Black.png",
                "XB360_RightStick.png": "XB360_RightStick_Black.png",
            },
        },
    },
]

LAYOUT_CLASS = {
    "DualSense": "DualSenseLayout",
    "DS4": "DS4Layout",
    "XBOXSERIES": "XboxSeriesXLayout",
    "XBOXONE": "XboxOneSLayout",
    "XBOX360": "Xbox360Layout",
}


def layout_rows(cls):
    src = io.open(LAYOUT_CS, encoding="utf-8").read()
    blk = src.split("class %s" % cls, 1)[1].split("public static class", 1)[0]
    rows = re.findall(
        r'new\("([^"]*)",\s*"([^"]+)",\s*OverlayElementType\.(\w+),\s*'
        r'(-?\d+),\s*(-?\d+),\s*(\d+),\s*(\d+)', blk)
    return [(fn, t, ty, int(x), int(y), int(w), int(h))
            for fn, t, ty, x, y, w, h in rows]


def rest_sprites(cls):
    """The colorway-sensitive rest art: TriggerBase + StickRing rows."""
    return [(fn, ty == "TriggerBase", x, y, w, h)
            for fn, _, ty, x, y, w, h in layout_rows(cls)
            if ty in ("TriggerBase", "StickRing")]


def load(p):
    return np.asarray(Image.open(p).convert("RGBA"), dtype=np.uint8)


def _blur3(img):
    """3x3 box blur per channel. The rest sprites are line art; a subpixel
    phase difference between the shipped sprite's AA and the template's
    puts large RMSE on high-contrast outlines that are visually identical.
    Comparing blurred images cancels edge phase while a real color change
    (a black trigger vs a white one) fully survives."""
    f = img.astype(np.float64)
    p = np.pad(f, ((1, 1), (1, 1), (0, 0)), mode="edge")
    out = np.zeros_like(f)
    for dy in (0, 1, 2):
        for dx in (0, 1, 2):
            out += p[dy:dy + f.shape[0], dx:dx + f.shape[1]]
    return out / 9.0


def rmse(a, b, mask):
    if not mask.any():
        return 0.0
    fa, fb = _blur3(a), _blur3(b)
    d = fa[mask] - fb[mask]
    return float(np.sqrt((d * d).mean()))


def find_templates(cwdir, base_img, stick_boxes):
    """Prepared base + full render, discovered by content: candidates are
    same-canvas PNGs; the full render is the one that differs from the
    prepared base inside BOTH stick bboxes (sticks present)."""
    H, W = base_img.shape[:2]
    prepared = None
    candidates = []
    for f in sorted(os.listdir(cwdir)):
        if not f.lower().endswith(".png"):
            continue
        img = load(os.path.join(cwdir, f))
        if img.shape[:2] != (H, W):
            continue
        if "base" in f.lower():
            prepared = (f, img)
        else:
            candidates.append((f, img))
    if prepared is None:
        raise SystemExit("no prepared base in %s" % cwdir)

    def sticks_present(img):
        ok = 0
        for x, y, w, h in stick_boxes:
            a = img[y:y + h, x:x + w].astype(np.int16)
            b = prepared[1][y:y + h, x:x + w].astype(np.int16)
            if np.abs(a - b).mean() > 3.0:
                ok += 1
        return ok == len(stick_boxes)

    fulls = [(f, img) for f, img in candidates if sticks_present(img)]
    if not fulls:
        raise SystemExit("no full render found in %s" % cwdir)
    # Several qualify (e.g. "(No Lightbar)" variants also carry sticks);
    # any works for the four bboxes, prefer the shortest name (the plain
    # full overlay).
    fulls.sort(key=lambda t: len(t[0]))
    return prepared, fulls[0]


def crop_like(img, shipped, box):
    """Crop at the layout position in the shipped sprite's real pixel size
    (clamped at every canvas edge, zero-padded outside)."""
    x, y, _, _ = box
    sh, sw = shipped.shape[:2]
    H, W = img.shape[:2]
    out = np.zeros_like(shipped)
    sy, sx = max(y, 0), max(x, 0)
    y2, x2 = min(y + sh, H), min(x + sw, W)
    if y2 > sy and x2 > sx:
        out[sy - y:y2 - y, sx - x:x2 - x] = img[sy:y2, sx:x2]
    return out


def crop_box(img, box, shipped_shape):
    """Template crop for a sprite. When the stored sprite is close to its
    layout box, crop at the sprite's own size (padding keeps it aligned).
    When it is stored larger than its render box (the X360 sticks ship
    211x185 into a 185x165 slot), crop the LAYOUT box, the region the
    view actually renders the file into, and resample up to the stored
    size so the variant substitutes the stock file shape-for-shape."""
    sh, sw = shipped_shape[:2]
    x, y, w, h = box
    if abs(sh - h) <= 6 and abs(sw - w) <= 6:
        fake = np.zeros((sh, sw, 4), dtype=np.uint8)
        return crop_like(img, fake, box)
    region = np.zeros((h, w, 4), dtype=np.uint8)
    fake = region
    region = crop_like(img, fake, box)
    im = Image.fromarray(region).resize((sw, sh), Image.LANCZOS)
    return np.array(im, dtype=np.uint8)


def extract(full_img, base_img, shipped, box, behind_base):
    """Variant sprite + the mask its fidelity is judged over.

    RGB comes from the template crop, alpha from the shipped sprite. A
    TriggerBase draws BEHIND the base render, so its pixels under the
    opaque body are invisible in-app AND unrecoverable from the template
    (the template shows body there, not trigger art): those keep the
    shipped sprite's own RGB, and the judgment mask excludes them.
    StickRings draw above the base and are judged in full."""
    cut = crop_box(full_img, box, shipped.shape)
    cut[..., 3] = shipped[..., 3]
    cut[shipped[..., 3] == 0] = 0
    mask = shipped[..., 3] >= 200
    if behind_base:
        # low threshold: the body's soft-alpha edge already carries body
        # pixels in the template (the X360 bumper's top edge), so any
        # partially-covered pixel is unreliable as trigger art
        covered = crop_box(base_img, box, shipped.shape)[..., 3] >= 32
        cut[covered & (shipped[..., 3] > 0), :3] = shipped[covered & (shipped[..., 3] > 0), :3]
        mask &= ~covered
    return cut, mask


def main():
    fam_results = []
    for fam in FAMILIES:
        folder, packdir = fam["folder"], fam["packdir"]
        print("== %s ==" % folder)
        appdir = os.path.join(APP2D, folder)
        stock_base = load(os.path.join(appdir, fam["base"]))
        sprites = rest_sprites(LAYOUT_CLASS[folder])
        stick_boxes = [(x, y, w, h) for fn, tb, x, y, w, h in sprites if not tb]
        shipped = {fn: load(os.path.join(appdir, fn)) for fn, *_ in sprites}
        # A shipped sprite may be a few pixels off its declared layout size
        # (the view stretches it; DualSense_L2 is 201x153 against a 201x152
        # row). Extraction follows the sprite's REAL size anchored at the
        # layout position, so variants stay pixel-identical in shape to the
        # stock file they substitute.
        for fn, tb, x, y, w, h in sprites:
            sh = shipped[fn].shape
            if sh[:2] != (h, w):
                print("  note: %s stored %dx%d, rendered %dx%d"
                      % (fn, sh[1], sh[0], w, h))

        # sweep this generator's previous emissions so a sprite that no
        # longer differs cannot survive as a stale variant file
        all_ids = [c[0] for c in fam["colorways"][1:]]
        for f in os.listdir(appdir):
            stem, ext = os.path.splitext(f)
            if ext.lower() == ".png" and any(stem.endswith("_" + i) for i in all_ids):
                os.remove(os.path.join(appdir, f))

        entries = []
        offsets = {}
        default_cut = {}
        emitted_pack = {}
        pack_sprites = fam.get("pack_sprites")
        default_id = fam["colorways"][0][0]
        for cid, cname, packcw in fam["colorways"]:
            cwdir = os.path.join(PACK, packdir, packcw)
            prepared, full = find_templates(cwdir, stock_base, stick_boxes)
            is_default = cid == default_id

            # prepared base must be the stock base for the default colorway
            if is_default:
                if not np.array_equal(prepared[1], stock_base):
                    raise SystemExit("%s default base %s != stock %s"
                                     % (folder, prepared[0], fam["base"]))

            overrides = {}
            report = []
            if pack_sprites is not None:
                # The pack ships this family's per-colorway rest sprites
                # itself; copy them verbatim instead of extracting. A pack
                # file shared by several colorways (the Series' black
                # triggers) is emitted once and referenced by each.
                for stock, packfn in (pack_sprites.get(cid) or {}).items():
                    src = os.path.join(PACK, fam["pack_sprites_dir"], packfn)
                    img = load(src)
                    if img.shape != shipped[stock].shape:
                        raise SystemExit(
                            "%s %s: pack sprite %s is %s, stock %s is %s"
                            % (folder, cid, packfn, img.shape[:2],
                               stock, shipped[stock].shape[:2]))
                    if packfn in emitted_pack:
                        vfn = emitted_pack[packfn]
                    else:
                        stem, ext = os.path.splitext(stock)
                        vfn = "%s_%s%s" % (stem, cid, ext)
                        import shutil
                        shutil.copyfile(src, os.path.join(appdir, vfn))
                        emitted_pack[packfn] = vfn
                    overrides[stock] = vfn
                    report.append("%s -> %s (pack)" % (stock, vfn))
                sprite_rows = []
            else:
                sprite_rows = sprites
            for fn, tb, x, y, w, h in sprite_rows:
                # The layout position can sit a pixel or two off the sprite's
                # true anchor (the shipped sprite sizes already disagree with
                # the layout rows by a pixel). Calibrate the anchor on the
                # DEFAULT colorway by minimizing the residual against the
                # shipped sprite, then hold that offset for every colorway:
                # the pack renders share one geometry.
                if is_default:
                    best = None
                    for dy in range(-8, 9):
                        for dx in range(-8, 9):
                            c, m = extract(full[1], stock_base, shipped[fn],
                                           (x + dx, y + dy, w, h), behind_base=tb)
                            e = rmse(c[..., :3], shipped[fn][..., :3], m)
                            if best is None or e < best[0]:
                                best = (e, dx, dy)
                    offsets[fn] = (best[1], best[2])
                dx, dy = offsets[fn]
                cut, mask = extract(full[1], stock_base, shipped[fn],
                                    (x + dx, y + dy, w, h), behind_base=tb)
                if is_default:
                    err = rmse(cut[..., :3], shipped[fn][..., :3], mask)
                    default_cut[fn] = cut
                    report.append("%s rmse=%.1f (d%+d%+d)" % (fn, err, dx, dy))
                    if err > VALIDATE_RMSE:
                        raise SystemExit(
                            "VALIDATION FAILED %s/%s: default extraction "
                            "rmse %.1f > %.1f" % (folder, fn, err, VALIDATE_RMSE))
                    continue
                err = rmse(cut[..., :3], default_cut[fn][..., :3], mask)
                if err >= EMIT_RMSE:
                    stem, ext = os.path.splitext(fn)
                    vfn = "%s_%s%s" % (stem, cid, ext)
                    Image.fromarray(cut).save(os.path.join(appdir, vfn))
                    overrides[fn] = vfn
                    report.append("%s -> %s (vs default %.1f)" % (fn, vfn, err))
                else:
                    report.append("%s shared (vs default %.1f)" % (fn, err))

            base_file = fam["base"]
            if not is_default:
                stem, ext = os.path.splitext(fam["base"])
                base_file = "%s_%s%s" % (stem, cid, ext)
                if np.array_equal(prepared[1], stock_base):
                    raise SystemExit("%s %s base identical to stock" % (folder, cid))
                Image.fromarray(prepared[1]).save(os.path.join(appdir, base_file))
            entries.append((cid, cname, base_file, overrides))
            print("  %-15s base=%-34s %s"
                  % (cid, base_file, "; ".join(report)))
        fam_results.append((folder, fam["family"], entries))
    return fam_results


def derive_edge(fam_results):
    """DualSense Edge colorway bases: the DualSense colorway base widened
    with the gutter tiles through the Edge generator's own function, plus
    the DualSense sprite variants copied under the Edge folder (its layout
    uses the same DualSense_* sprite names)."""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from gen_dualsense_edge_art import widen_with_tiles

    ds = next(r for r in fam_results if r[0] == "DualSense")
    ds_dir = os.path.join(APP2D, "DualSense")
    edge_dir = os.path.join(APP2D, "DUALSENSEEDGE")
    print("== DUALSENSEEDGE (derived) ==")
    entries = []
    for cid, cname, base_file, overrides in ds[2]:
        if base_file == "DualSense_base.png":
            edge_base = "DualSense_base.png"       # stock Edge base already built
        else:
            src = Image.open(os.path.join(ds_dir, base_file)).convert("RGBA")
            out, _ = widen_with_tiles(src)
            edge_base = base_file
            out.save(os.path.join(edge_dir, edge_base))
        e_over = {}
        for fn, vfn in overrides.items():
            import shutil
            shutil.copyfile(os.path.join(ds_dir, vfn), os.path.join(edge_dir, vfn))
            e_over[fn] = vfn
        entries.append((cid, cname, edge_base, e_over))
        print("  %-15s base=%-34s %d sprite override(s)"
              % (cid, edge_base, len(e_over)))
    fam_results.append(("DUALSENSEEDGE", "DualSenseEdge", entries))


def emit_registry(fam_results):
    lines = []
    w = lines.append
    w("// <auto-generated>")
    w("// Generated by tools/gen_2d_colorways.py from the Gamepad-Asset-Pack")
    w("// colorway templates. Do not edit by hand: rerun the generator.")
    w("// </auto-generated>")
    w("")
    w("using System.Collections.Generic;")
    w("")
    w("namespace PadForge.Models2D")
    w("{")
    w("    /// <summary>One 2D colorway: the appearance id it answers to")
    w("    /// (shared with the 3D model families where the same physical")
    w("    /// colorway exists), its display name, its base render, and the")
    w("    /// rest-art sprite files that differ from the default's.</summary>")
    w("    public sealed class Colorway2D")
    w("    {")
    w("        public string Id { get; }")
    w("        public string Name { get; }")
    w("        public string BaseFile { get; }")
    w("        public IReadOnlyDictionary<string, string> Overrides { get; }")
    w("        public Colorway2D(string id, string name, string baseFile,")
    w("            IReadOnlyDictionary<string, string> overrides)")
    w("        { Id = id; Name = name; BaseFile = baseFile; Overrides = overrides; }")
    w("    }")
    w("")
    w("    /// <summary>2D colorway sets per asset folder. The family key is")
    w("    /// the per-pad appearance store's key (PadSetting.Model3DAppearances),")
    w("    /// the same one the 3D picker writes, so one selection drives both")
    w("    /// views; a view without art for the chosen id falls back to its")
    w("    /// default. Entry 0 is the default (the stock files).</summary>")
    w("    public static class Controller2DColorways")
    w("    {")
    w("        private static readonly IReadOnlyDictionary<string, string> None")
    w("            = new Dictionary<string, string>();")
    w("")
    empty = "None"
    sets = []
    for folder, family, entries in fam_results:
        field = "Set_" + folder
        sets.append((folder, family, field))
        w("        private static readonly Colorway2D[] %s =" % field)
        w("        {")
        for cid, cname, base_file, overrides in entries:
            if overrides:
                ov = ("new Dictionary<string, string> { "
                      + ", ".join('{ "%s", "%s" }' % kv for kv in sorted(overrides.items()))
                      + " }")
            else:
                ov = empty
            w('            new("%s", "%s", "%s",' % (cid, cname, base_file))
            w("                %s)," % ov)
        w("        };")
        w("")
    w("        /// <summary>The colorway set and appearance-store family key")
    w("        /// for a 2D asset folder; null set when the folder has no")
    w("        /// colorways.</summary>")
    w("        public static (string FamilyKey, Colorway2D[] Set) For(string folder) => folder switch")
    w("        {")
    for folder, family, field in sets:
        w('            "%s" => ("%s", %s),' % (folder, family, field))
    w("            _ => (null, null),")
    w("        };")
    w("    }")
    w("}")
    io.open(REGISTRY_CS, "w", encoding="utf-8", newline="\r\n").write("\n".join(lines) + "\n")
    print("wrote %s" % REGISTRY_CS)


if __name__ == "__main__":
    results = main()
    derive_edge(results)
    emit_registry(results)
