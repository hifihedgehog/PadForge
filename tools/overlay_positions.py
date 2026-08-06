#!/usr/bin/env python3
"""
Extract overlay positions from Gamepad-Asset-Pack SVG files.

Parses the full controller layout SVGs which have labeled elements at their correct
positions. Extracts bounding boxes and converts to pixel coordinates using the SVG's
export DPI. Outputs a C# source file.

Usage:
    pip install svgpathtools lxml opencv-python numpy
    python tools/overlay_positions.py
"""

import os
import sys
import re
import numpy as np
from lxml import etree
from svgpathtools import parse_path
import cv2

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MODELS_DIR = os.path.join(PROJ_ROOT, "PadForge.App", "2DModels")
ASSET_PACK = os.path.join(os.path.dirname(PROJ_ROOT), "Gamepad-Asset-Pack", "Controller Asset Pack")

NS = {
    'svg': 'http://www.w3.org/2000/svg',
    'inkscape': 'http://www.inkscape.org/namespaces/inkscape',
}


def parse_transform(transform_str):
    """Parse SVG transform string into a 3x3 matrix."""
    if not transform_str:
        return np.eye(3)
    result = np.eye(3)
    for match in re.finditer(r'(\w+)\s*\(([^)]+)\)', transform_str):
        func, args_str = match.group(1), match.group(2).strip()
        args = [float(x) for x in re.split(r'[,\s]+', args_str)]
        m = np.eye(3)
        if func == 'translate':
            m[0, 2] = args[0]
            m[1, 2] = args[1] if len(args) > 1 else 0
        elif func == 'matrix':
            m[0, 0], m[1, 0], m[0, 1], m[1, 1], m[0, 2], m[1, 2] = args[:6]
        elif func == 'scale':
            m[0, 0] = args[0]
            m[1, 1] = args[1] if len(args) > 1 else args[0]
        elif func == 'rotate':
            a = np.radians(args[0])
            m[0, 0], m[0, 1], m[1, 0], m[1, 1] = np.cos(a), -np.sin(a), np.sin(a), np.cos(a)
        result = result @ m
    return result


def transform_bbox(matrix, xmin, ymin, w, h):
    """Transform a bounding box through a matrix, returning new axis-aligned bbox."""
    corners = np.array([
        [xmin, ymin, 1], [xmin + w, ymin, 1],
        [xmin, ymin + h, 1], [xmin + w, ymin + h, 1]
    ]).T
    transformed = matrix @ corners
    xs, ys = transformed[0], transformed[1]
    return float(xs.min()), float(ys.min()), float(xs.max() - xs.min()), float(ys.max() - ys.min())


def get_cumulative_transform(elem):
    """Walk up element tree to compute cumulative transform."""
    transforms = []
    current = elem
    while current is not None:
        t = current.get('transform')
        if t:
            transforms.append(parse_transform(t))
        current = current.getparent()
    result = np.eye(3)
    for t in reversed(transforms):
        result = result @ t
    return result


def element_bbox(elem):
    """Compute bounding box of a single SVG element in its local coordinate space."""
    tag = etree.QName(elem.tag).localname if '}' in elem.tag else elem.tag
    if tag == 'path':
        d = elem.get('d')
        if d:
            try:
                path = parse_path(d)
                if len(path) > 0:
                    xmin, xmax, ymin, ymax = path.bbox()
                    return xmin, ymin, xmax - xmin, ymax - ymin
            except Exception:
                pass
    elif tag in ('ellipse', 'circle'):
        cx = float(elem.get('cx', 0))
        cy = float(elem.get('cy', 0))
        rx = float(elem.get('rx', elem.get('r', 0)))
        ry = float(elem.get('ry', elem.get('r', 0)))
        return cx - rx, cy - ry, 2 * rx, 2 * ry
    elif tag == 'rect':
        x = float(elem.get('x', 0))
        y = float(elem.get('y', 0))
        w = float(elem.get('width', 0))
        h = float(elem.get('height', 0))
        return x, y, w, h
    return None


def group_bbox(group_elem):
    """Compute combined bounding box of all visual children of a group."""
    bboxes = []
    for child in group_elem.iter():
        if child is group_elem:
            continue
        bb = element_bbox(child)
        if bb:
            transform = get_cumulative_transform(child)
            # Remove the group's own ancestors from the child transform to get child-relative-to-group
            # Actually, we want the absolute transform for the child
            bboxes.append(transform_bbox(transform, *bb))

    if not bboxes:
        return None
    xmin = min(b[0] for b in bboxes)
    ymin = min(b[1] for b in bboxes)
    xmax = max(b[0] + b[2] for b in bboxes)
    ymax = max(b[1] + b[3] for b in bboxes)
    return xmin, ymin, xmax - xmin, ymax - ymin


def find_element_by_label(root, label):
    """Find first element with matching inkscape:label."""
    for elem in root.iter():
        if elem.get('{http://www.inkscape.org/namespaces/inkscape}label') == label:
            return elem
    return None


def get_element_pixel_bbox(root, label, scale):
    """Get pixel bounding box for a labeled element."""
    elem = find_element_by_label(root, label)
    if elem is None:
        return None

    tag = etree.QName(elem.tag).localname if '}' in elem.tag else elem.tag

    if tag == 'g':
        bbox = group_bbox(elem)
    else:
        bb = element_bbox(elem)
        if bb:
            transform = get_cumulative_transform(elem)
            bbox = transform_bbox(transform, *bb)
        else:
            bbox = None

    if bbox:
        return (
            round(bbox[0] * scale),
            round(bbox[1] * scale),
            round(bbox[2] * scale),
            round(bbox[3] * scale),
        )
    return None


def center_overlay_on_bbox(bbox, overlay_path):
    """Center an overlay image on a bounding box center. Returns (x, y, w, h)."""
    if not os.path.exists(overlay_path):
        return bbox
    ov = cv2.imread(overlay_path, cv2.IMREAD_UNCHANGED)
    ov_w, ov_h = ov.shape[1], ov.shape[0]
    cx = bbox[0] + bbox[2] / 2
    cy = bbox[1] + bbox[3] / 2
    return (round(cx - ov_w / 2), round(cy - ov_h / 2), ov_w, ov_h)


def fit_overlay_to_bbox(bbox, overlay_path, scale=1.0):
    """Resize the overlay PNG to fit the SVG bbox (preserving the asset's
    aspect ratio) and center it on the bbox center. Returns (x, y, w, h)
    using the FITTED size — which then becomes the rendered overlay's size.

    The asset pack's per-target press-overlay PNGs ship at scales that
    sometimes match the base (Xbox 360, DS4) and sometimes don't (Xbox One
    face buttons authored too small, Xbox Series authored at 2x). The SVG's
    inkscape:label bbox is the authoritative measurement of where and how
    big each button appears on the controller, so we resize the PNG to fit
    the bbox rather than trusting the PNG's native dimensions.

    `scale` lets callers nudge a category-wide multiplier (e.g., bumpers
    sometimes want to overflow their SVG group bbox slightly).
    """
    if bbox is None or not os.path.exists(overlay_path):
        return bbox
    ov = cv2.imread(overlay_path, cv2.IMREAD_UNCHANGED)
    if ov is None:
        return bbox
    bx, by, bw, bh = bbox
    target_w = max(1, int(round(bw * scale)))
    target_h = max(1, int(round(bh * scale)))
    # Preserve PNG aspect ratio: pick the dimension that fills the bbox
    # without overflowing, then center.
    ov_aspect = ov.shape[1] / ov.shape[0]
    box_aspect = target_w / target_h
    if ov_aspect > box_aspect:
        new_w = target_w
        new_h = max(1, int(round(target_w / ov_aspect)))
    else:
        new_h = target_h
        new_w = max(1, int(round(target_h * ov_aspect)))
    if (ov.shape[1], ov.shape[0]) != (new_w, new_h):
        scaled = cv2.resize(ov, (new_w, new_h), interpolation=cv2.INTER_LANCZOS4)
        cv2.imwrite(overlay_path, scaled)
    cx = bx + bw / 2.0
    cy = by + bh / 2.0
    return (round(cx - new_w / 2.0), round(cy - new_h / 2.0), new_w, new_h)


def refine_via_base_template(base_path, results, ov_dir, targets, search_radius=80, conf_threshold=0.4):
    """For each named target, template-match its press-overlay alpha against
    the BASE PNG's dark-detail map (small drawn details like button labels,
    Start/Back glyphs, screen edges show up as dark on the white body).
    Restricted to a small radius around the current (SVG-derived) position
    to keep matches local. Updates the layout entry's (x, y) when a
    high-confidence match is found; size is left alone since this refines
    POSITION only, not scale.

    Used for tiny buttons (Start / Back / Create / Option) whose SVG
    inkscape:label can sit on the wrong element (a text label, an icon
    centroid, etc.) and produce a position that's a few-to-many pixels
    off from the actual visible button on the rendered controller body.
    Asset-pack composite overlays don't help because they're just the body
    silhouette without highlights, but the BASE PNG itself has the small
    button drawn as detail."""
    base = cv2.imread(base_path, cv2.IMREAD_UNCHANGED)
    if base is None: return results
    gray = cv2.cvtColor(base[:, :, :3], cv2.COLOR_BGR2GRAY).astype(np.float32)
    dark = 255.0 - gray
    if base.shape[2] >= 4:
        dark[base[:, :, 3] < 200] = 0
    H, W = dark.shape
    target_set = set(targets)

    refined = []
    for filename, target, etype, x, y, w, h in results:
        if target not in target_set or not filename:
            refined.append((filename, target, etype, x, y, w, h))
            continue

        ov = cv2.imread(os.path.join(ov_dir, filename), cv2.IMREAD_UNCHANGED)
        if ov is None or ov.shape[2] < 4:
            refined.append((filename, target, etype, x, y, w, h))
            continue

        ov_alpha = ov[:, :, 3].astype(np.float32)
        oh, ow = ov_alpha.shape
        if oh > H or ow > W:
            refined.append((filename, target, etype, x, y, w, h))
            continue

        try:
            result = cv2.matchTemplate(dark, ov_alpha, cv2.TM_CCOEFF_NORMED)
        except cv2.error:
            refined.append((filename, target, etype, x, y, w, h))
            continue

        # Restrict candidate positions by centroid distance to the current
        # (SVG-derived) target centroid.
        ref_cx = x + w / 2.0
        ref_cy = y + h / 2.0
        rh_, rw_ = result.shape
        ty_ix, tx_ix = np.indices((rh_, rw_))
        cxs = tx_ix + ow / 2.0
        cys = ty_ix + oh / 2.0
        dist = np.hypot(cxs - ref_cx, cys - ref_cy)
        masked = np.where(dist <= search_radius, result, -np.inf)
        if not np.isfinite(masked).any():
            refined.append((filename, target, etype, x, y, w, h))
            continue
        max_val = float(masked.max())
        idx = np.unravel_index(np.argmax(masked), masked.shape)
        rx, ry = int(idx[1]), int(idx[0])

        if max_val >= conf_threshold and (abs(rx - x) > 1 or abs(ry - y) > 1):
            print(f"  BASE-MATCH {target:20s}: ({x},{y}) -> ({rx},{ry}) conf={max_val:.3f}")
            refined.append((filename, target, etype, rx, ry, w, h))
        else:
            refined.append((filename, target, etype, x, y, w, h))

    return refined


def refine_via_alpha_diff(base_path, composite_path, results, ov_dir):
    """Visual analysis: the asset pack's "Controller Overlay" PNG is the
    base body + every press highlight composited together. Subtract the
    base alpha channel from the overlay alpha and the residual is exactly
    the union of press highlights. Connected-component labeling of that
    residual gives one bbox per highlight, which we match to each layout
    target by proximity to the SVG-derived centroid.

    This replaces guesswork (per-element scale percentages) and per-target
    template matching with one direct measurement of where and how big
    each highlight ACTUALLY appears in the asset pack's composite. As a
    side effect the press overlay PNGs on disk are resized to match their
    measured bbox, so subsequent rendering uses the right pixel sizes."""
    base = cv2.imread(base_path, cv2.IMREAD_UNCHANGED)
    comp = cv2.imread(composite_path, cv2.IMREAD_UNCHANGED)
    if base is None or comp is None:
        print("  WARNING: base or composite missing; skipping alpha-diff refinement")
        return results
    if base.shape != comp.shape:
        print(f"  WARNING: base {base.shape} != composite {comp.shape}; skipping alpha-diff refinement")
        return results

    base_alpha = base[:, :, 3].astype(int)
    comp_alpha = comp[:, :, 3].astype(int)
    # Pixels where the composite is more opaque than the base ARE the
    # press-highlight regions. Threshold above anti-alias noise.
    diff = (comp_alpha - base_alpha).astype(np.int32)
    mask = (diff > 16).astype(np.uint8)

    # Connected components — one blob per visible highlight.
    num_labels, labels, stats, centroids = cv2.connectedComponentsWithStats(mask, connectivity=8)
    blobs = []  # list of (cx, cy, x, y, w, h, area)
    for li in range(1, num_labels):
        x, y, w, h, area = stats[li]
        if area < 80:  # discard tiny anti-alias specks
            continue
        blobs.append((float(centroids[li][0]), float(centroids[li][1]), int(x), int(y), int(w), int(h), int(area)))
    print(f"  alpha-diff blobs: {len(blobs)}")

    used = set()
    refined = []
    for filename, target, etype, x, y, w, h in results:
        if not filename:
            # Manual zone (e.g., DualSense touchpad) — pass through unchanged.
            refined.append((filename, target, etype, x, y, w, h))
            continue

        ref_cx = x + w / 2.0
        ref_cy = y + h / 2.0

        # Score blobs: distance + a small size-penalty for blobs that are
        # *much* bigger than the target's hint size, so a single huge blob
        # (e.g., a full Xbox guide LED ring) doesn't capture small targets
        # that happen to sit nearby.
        best = None  # (score, blob_idx)
        for bi, (bcx, bcy, bx, by, bw, bh, area) in enumerate(blobs):
            if bi in used: continue
            dist = ((bcx - ref_cx) ** 2 + (bcy - ref_cy) ** 2) ** 0.5
            # Cap distance to a reasonable physical neighborhood — we
            # allow ~150 px movement which covers any plausible scale
            # change but rejects far-side matches.
            if dist > 200: continue
            if best is None or dist < best[0]:
                best = (dist, bi)

        if best is None:
            print(f"  KEEP   {target:20s}: no blob within range, keeping SVG position {ov_w0_str(filename, ov_dir)}")
            refined.append((filename, target, etype, x, y, w, h))
            continue

        used.add(best[1])
        bcx, bcy, bx, by, bw, bh, area = blobs[best[1]]

        # Resize the source PNG to the measured blob size so that when the
        # 2D view paints the overlay at this rect, it fills the correct
        # area without aspect distortion. Preserve the PNG's aspect: pick
        # the dimension closest to the blob's aspect.
        ov_path = os.path.join(ov_dir, filename)
        ov = cv2.imread(ov_path, cv2.IMREAD_UNCHANGED)
        if ov is not None and ov.shape[2] >= 4 and (ov.shape[1] != bw or ov.shape[0] != bh):
            scaled = cv2.resize(ov, (bw, bh), interpolation=cv2.INTER_LANCZOS4)
            cv2.imwrite(ov_path, scaled)
            print(f"  BLOB   {target:20s}: bbox=({bx},{by}) {bw}x{bh}  PNG {ov.shape[1]}x{ov.shape[0]} -> {bw}x{bh}  dist={best[0]:.0f}")
        else:
            print(f"  BLOB   {target:20s}: bbox=({bx},{by}) {bw}x{bh}  dist={best[0]:.0f}")

        refined.append((filename, target, etype, bx, by, bw, bh))

    return refined


def ov_w0_str(filename, ov_dir):
    p = os.path.join(ov_dir, filename)
    if not os.path.exists(p): return "(missing)"
    im = cv2.imread(p, cv2.IMREAD_UNCHANGED)
    return f"({im.shape[1]}x{im.shape[0]})" if im is not None else "(unreadable)"


def refine_with_composite(composite_path, results, search_radius=40, multiscale=False,
                          scale_min=0.5, scale_max=2.0, scale_step=0.05,
                          confidence_threshold=0.3):
    """Refine overlay positions (and optionally sizes) by alpha-channel
    template matching against the composite overlay PNG.

    Default mode (multiscale=False): single-scale matching at the overlay's
    native pixel size, restricted to a neighborhood of the SVG-derived
    position. This is what Xbox 360 / DS4 / DualSense use — their press
    overlays already ship at the right scale.

    Multi-scale mode: try a range of scale factors against the entire
    composite, pick the best (scale, position) per overlay, AND resize the
    overlay PNG on disk to match the chosen scale. Used by Xbox One / Xbox
    Series, where the asset pack ships press overlays at scales that don't
    match the base PNG (Xbox One face buttons authored at ~5.6% of base
    width vs Xbox 360's 8.2%, Xbox Series press overlays authored at 2x of
    base scale). Visual analysis instead of hand-tuned percentages."""
    composite = cv2.imread(composite_path, cv2.IMREAD_UNCHANGED)
    if composite is None or composite.shape[2] < 4:
        print("  WARNING: Could not load composite overlay for refinement")
        return results

    comp_alpha = composite[:, :, 3].astype(np.float32)
    comp_h, comp_w = comp_alpha.shape

    refined = []
    for filename, target, etype, x, y, w, h in results:
        if not filename:
            # Manual zone (e.g., DualSense touchpad); no PNG to match.
            refined.append((filename, target, etype, x, y, w, h))
            continue

        overlay_path = os.path.join(os.path.dirname(composite_path), filename)
        ov = cv2.imread(overlay_path, cv2.IMREAD_UNCHANGED)
        if ov is None or ov.shape[2] < 4:
            refined.append((filename, target, etype, x, y, w, h))
            continue

        ov_alpha = ov[:, :, 3].astype(np.float32)
        ov_h0, ov_w0 = ov_alpha.shape

        if multiscale:
            # Center the search around the SVG-derived (x, y) so we don't
            # match a stylistically-similar highlight on the OTHER side of
            # the controller. Reference position = the centroid of the SVG
            # element, scaled to PNG pixels:
            ref_cx = x + ov_w0 / 2.0
            ref_cy = y + ov_h0 / 2.0
            # Allow a generous radius — scaled overlays can shift position
            # several tens of pixels when growing/shrinking, but not jump
            # to a completely different button.
            position_radius = max(80, max(ov_w0, ov_h0))

            best = None  # (confidence, scale, x_pos, y_pos, scaled_w, scaled_h)
            for scale_pct in range(int(scale_min * 100), int(scale_max * 100) + 1, int(scale_step * 100)):
                s = scale_pct / 100.0
                new_w = max(1, int(round(ov_w0 * s)))
                new_h = max(1, int(round(ov_h0 * s)))
                if new_w > comp_w or new_h > comp_h:
                    continue
                scaled = cv2.resize(ov_alpha, (new_w, new_h), interpolation=cv2.INTER_LANCZOS4)
                try:
                    result = cv2.matchTemplate(comp_alpha, scaled, cv2.TM_CCOEFF_NORMED)
                except cv2.error:
                    continue
                # Restrict candidate positions: the matched template's
                # CENTER must lie within position_radius of the reference
                # centroid. matchTemplate result[ty,tx] = match score for
                # template's TOP-LEFT at (tx,ty); convert to center.
                rh, rw = result.shape
                # Build mask of valid (tx, ty)
                tys, txs = np.indices((rh, rw))
                cxs = txs + new_w / 2.0
                cys = tys + new_h / 2.0
                dist = np.hypot(cxs - ref_cx, cys - ref_cy)
                masked = np.where(dist <= position_radius, result, -np.inf)
                if not np.isfinite(masked).any():
                    continue
                max_val = float(masked.max())
                idx = np.unravel_index(np.argmax(masked), masked.shape)
                tx_best, ty_best = int(idx[1]), int(idx[0])
                if best is None or max_val > best[0]:
                    best = (max_val, s, tx_best, ty_best, new_w, new_h)

            if best is not None and best[0] > confidence_threshold:
                conf, s, rx, ry, new_w, new_h = best
                if abs(s - 1.0) > 0.02:
                    # Resize the source PNG on disk to the matched scale so
                    # subsequent rendering uses the correct pixel size.
                    scaled_full = cv2.resize(ov, (new_w, new_h), interpolation=cv2.INTER_LANCZOS4)
                    cv2.imwrite(overlay_path, scaled_full)
                    print(f"  RESIZE {target:20s}: {ov_w0}x{ov_h0} -> {new_w}x{new_h} @ s={s:.2f} conf={conf:.3f} pos=({rx},{ry})")
                else:
                    print(f"  REFINE {target:20s}: pos=({x},{y}) -> ({rx},{ry}) conf={conf:.3f}")
                refined.append((filename, target, etype, rx, ry, new_w, new_h))
            else:
                conf = best[0] if best else 0.0
                print(f"  SKIP   {target:20s}: low multi-scale confidence {conf:.3f}, keeping SVG position")
                refined.append((filename, target, etype, x, y, w, h))
            continue

        # Single-scale (default) — search around SVG-derived position.
        sx = max(0, x - search_radius)
        sy = max(0, y - search_radius)
        ex = min(comp_w, x + ov_w0 + search_radius)
        ey = min(comp_h, y + ov_h0 + search_radius)
        if ex - sx < ov_w0 or ey - sy < ov_h0:
            refined.append((filename, target, etype, x, y, w, h))
            continue
        search_region = comp_alpha[sy:ey, sx:ex]

        try:
            result = cv2.matchTemplate(search_region, ov_alpha, cv2.TM_CCOEFF_NORMED)
            _, max_val, _, max_loc = cv2.minMaxLoc(result)

            if max_val > confidence_threshold:
                rx = sx + max_loc[0]
                ry = sy + max_loc[1]
                delta = abs(rx - x) + abs(ry - y)
                if delta > 0:
                    print(f"  REFINE {target:20s}: ({x:4d},{y:4d}) -> ({rx:4d},{ry:4d}) conf={max_val:.3f} delta={delta}")
                refined.append((filename, target, etype, rx, ry, w, h))
            else:
                print(f"  SKIP   {target:20s}: low confidence {max_val:.3f}, keeping SVG position")
                refined.append((filename, target, etype, x, y, w, h))
        except cv2.error:
            refined.append((filename, target, etype, x, y, w, h))

    return refined


def process_xbox360():
    """Extract Xbox 360 overlay positions."""
    svg_path = os.path.join(ASSET_PACK,
        "Xbox 360 Controller Images", "Default Theme", "Theme SVG",
        "Xbox 360 VSCView - White.svg")

    tree = etree.parse(svg_path)
    root = tree.getroot()

    # Xbox SVG: mm units, 95.9851 DPI
    scale = 95.9851 / 25.4  # mm to pixels

    base = cv2.imread(os.path.join(MODELS_DIR, "XBOX360", "XB360_base.png"), cv2.IMREAD_UNCHANGED)
    ov_dir = os.path.join(MODELS_DIR, "XBOX360")

    results = []

    def add(svg_label, filename, target, elem_type, use_group=False):
        bbox = get_element_pixel_bbox(root, svg_label, scale)
        if bbox is None:
            print(f"  MISS: {svg_label}")
            return bbox
        overlay_path = os.path.join(ov_dir, filename)
        pos = center_overlay_on_bbox(bbox, overlay_path)
        results.append((filename, target, elem_type, pos[0], pos[1], pos[2], pos[3]))
        print(f"  {target:20s} ({svg_label:20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")
        return bbox

    print("Parsing Xbox 360 SVG elements...")

    # Face buttons (individual groups with Color/Outline/Text children)
    add("A Button", "XB360_A_Button.png", "ButtonA", "Button")
    add("B Button", "XB360_B_Button.png", "ButtonB", "Button")
    add("X Button", "XB360_X_Button.png", "ButtonX", "Button")
    add("Y Button", "XB360_Y_Button.png", "ButtonY", "Button")

    # Bumpers
    add("Left Bumper", "XB360_LeftBumper_Active.png", "LeftShoulder", "Button")
    add("Right Bumper", "XB360_RightBumper_Active.png", "RightShoulder", "Button")

    # Triggers
    add("Left Trigger", "XB360_LeftTrigger_Active.png", "LeftTrigger", "Trigger")
    add("Right Trigger", "XB360_RightTrigger_Active.png", "RightTrigger", "Trigger")

    # Back/Start
    add("Back Button", "XB360_BackButton.png", "ButtonBack", "Button")
    add("Start Button", "XB360_StartButton.png", "ButtonStart", "Button")

    # Guide button — use "Xbox Button" sub-group (not the full "Xbox Guide Button" group with LEDs)
    guide_bbox = get_element_pixel_bbox(root, "Xbox Button", scale)
    if guide_bbox is None:
        guide_bbox = get_element_pixel_bbox(root, "Xbox Guide Button", scale)
    if guide_bbox:
        pos = center_overlay_on_bbox(guide_bbox, os.path.join(ov_dir, "XB360_GuideButton.png"))
        results.append(("XB360_GuideButton.png", "ButtonGuide", "Button", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {'ButtonGuide':20s} ({'Xbox Button':20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")

    # Sticks (for ring overlays)
    add("Left Stick", "XB360_LeftStick.png", "LeftThumbRing", "StickRing")
    add("Right Stick", "XB360_RightStick.png", "RightThumbRing", "StickRing")

    # Stick clicks — same position as sticks
    left_stick_bbox = get_element_pixel_bbox(root, "Left Stick", scale)
    right_stick_bbox = get_element_pixel_bbox(root, "Right Stick", scale)
    if left_stick_bbox:
        pos = center_overlay_on_bbox(left_stick_bbox, os.path.join(ov_dir, "XB360_LeftStick_Click.png"))
        results.append(("XB360_LeftStick_Click.png", "LeftThumbButton", "StickClick", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {'LeftThumbButton':20s} ({'Left Stick':20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")
    if right_stick_bbox:
        pos = center_overlay_on_bbox(right_stick_bbox, os.path.join(ov_dir, "XB360_RightStick_Click.png"))
        results.append(("XB360_RightStick_Click.png", "RightThumbButton", "StickClick", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {'RightThumbButton':20s} ({'Right Stick':20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")

    # D-PAD — compute quadrants from "Regular D-PAD" group bbox
    dpad_bbox = get_element_pixel_bbox(root, "Regular D-PAD", scale)
    if dpad_bbox:
        dx, dy, dw, dh = dpad_bbox
        cx, cy = dx + dw / 2, dy + dh / 2

        # Up: top half center
        up_ov = os.path.join(ov_dir, "XB360_D-PAD_Up.png")
        ov = cv2.imread(up_ov, cv2.IMREAD_UNCHANGED)
        results.append(("XB360_D-PAD_Up.png", "DPadUp", "Button",
                        round(cx - ov.shape[1] / 2), round(dy - ov.shape[0] * 0.1),
                        ov.shape[1], ov.shape[0]))
        print(f"  {'DPadUp':20s} ({'D-PAD computed':20s}) -> ({results[-1][3]:4d}, {results[-1][4]:4d}) {results[-1][5]:4d}x{results[-1][6]:3d}")

        # Down: bottom half center
        ov = cv2.imread(os.path.join(ov_dir, "XB360_D-PAD_Down.png"), cv2.IMREAD_UNCHANGED)
        results.append(("XB360_D-PAD_Down.png", "DPadDown", "Button",
                        round(cx - ov.shape[1] / 2), round(dy + dh - ov.shape[0] * 0.9),
                        ov.shape[1], ov.shape[0]))
        print(f"  {'DPadDown':20s} ({'D-PAD computed':20s}) -> ({results[-1][3]:4d}, {results[-1][4]:4d}) {results[-1][5]:4d}x{results[-1][6]:3d}")

        # Left: left half center
        ov = cv2.imread(os.path.join(ov_dir, "XB360_D-PAD_Left.png"), cv2.IMREAD_UNCHANGED)
        results.append(("XB360_D-PAD_Left.png", "DPadLeft", "Button",
                        round(dx - ov.shape[1] * 0.1), round(cy - ov.shape[0] / 2),
                        ov.shape[1], ov.shape[0]))
        print(f"  {'DPadLeft':20s} ({'D-PAD computed':20s}) -> ({results[-1][3]:4d}, {results[-1][4]:4d}) {results[-1][5]:4d}x{results[-1][6]:3d}")

        # Right: right half center
        ov = cv2.imread(os.path.join(ov_dir, "XB360_D-PAD_Right.png"), cv2.IMREAD_UNCHANGED)
        results.append(("XB360_D-PAD_Right.png", "DPadRight", "Button",
                        round(dx + dw - ov.shape[1] * 0.9), round(cy - ov.shape[0] / 2),
                        ov.shape[1], ov.shape[0]))
        print(f"  {'DPadRight':20s} ({'D-PAD computed':20s}) -> ({results[-1][3]:4d}, {results[-1][4]:4d}) {results[-1][5]:4d}x{results[-1][6]:3d}")

    # Refine positions using full composite overlay
    composite_path = os.path.join(ov_dir, "Xbox 360 Controller Overlay.png")
    print("\nRefining Xbox 360 positions via alpha-channel template matching...")
    results = refine_with_composite(composite_path, results)

    # Tighten Start/Back/Guide positions against the base PNG — the SVG
    # labels for these tiny buttons sit on label-text centroids that are
    # a few px off the button silhouette. The press-overlay vs base
    # template match has lower confidence here because the highlight
    # shape doesn't match the dark button label exactly, but a 0.3
    # threshold is reliable enough for the small allowed shift.
    base_path = os.path.join(MODELS_DIR, "XBOX360", "XB360_base.png")
    print("Refining Xbox 360 small-button positions via base alpha template...")
    results = refine_via_base_template(base_path, results, ov_dir,
        targets={"ButtonBack", "ButtonStart", "ButtonGuide"},
        search_radius=40, conf_threshold=0.3)
    # Final fallback: locate Back/Start by detecting their drawn dark
    # silhouette in the base PNG's middle band. The press-overlay
    # template-match can land 10+ px off because the highlight shape
    # differs from the dark label glyph; centroid-of-dark-spot is more
    # robust for tiny labeled buttons.
    results = _xbox360_align_back_start_to_dark_spots(base_path, results)
    # Trigger / bumper / stick canonical positions: detected by sampling
    # the dark outlines in the asset pack's "Xbox 360 Controller Overlay"
    # composite (the inactive-state design). SVG-based bbox + composite
    # alpha-template matching land close but miss the silhouette edges by
    # 5-30 px because the overlay PNGs include curved alpha falloff that
    # confuses geometric centroid alignment. The canonical bboxes match
    # the trigger curve to the bumper curve with neither overlap nor gap.
    results = _xbox360_apply_canonical_overrides(results)

    return {"base_width": base.shape[1], "base_height": base.shape[0], "results": results}


def _xbox360_apply_canonical_overrides(results):
    """Override trigger / stick positions with values detected from the
    asset pack's inactive-state composite outline. Triggers extend
    dynamically so their bottom edge meets the (un-touched) shoulder
    overlay top. Sticks are sized to the actual housing silhouette so
    the press highlight doesn't extend past the rendered stick."""
    # Shoulders are NOT overridden — the SVG-bbox + base-template path
    # already lands them correctly and the user has signed off on their
    # current size/position.
    shoulder_y = {}
    for filename, target, etype, x, y, w, h in results:
        if target in ("LeftShoulder", "RightShoulder"):
            shoulder_y[target] = y

    # D-pad: position each arrow INSIDE the rendered cross silhouette
    # so its outer edge sits on the cross arm tip (not on the
    # surrounding well rim, where the SVG-bbox places it). Cross
    # silhouette body detected by connected-component analysis at
    # bbox (414, 626) 247x202; with outline buffer the visible cross
    # extends roughly to (410, 622)-(665, 832). Each arrow bbox is
    # sized so its rendered alpha shape fills its arm without
    # crossing the arm tip outline.
    dpad_overrides = {
        # Up: bottom anchored at y=732, stretched upward to h=122 (top y=610).
        "DPadUp":    (482, 610, 110, 122),
        # Down: unchanged from previously confirmed-good position.
        "DPadDown":  (482, 720, 110, 112),
        # Left/Right: top anchored at y=672, extended downward to h=105.
        "DPadLeft":  (410, 672, 134, 105),
        "DPadRight": (530, 672, 135, 105),
    }
    out2 = []
    for filename, target, etype, x, y, w, h in results:
        if target in dpad_overrides:
            nx, ny, nw, nh = dpad_overrides[target]
            out2.append((filename, target, etype, nx, ny, nw, nh))
        else:
            out2.append((filename, target, etype, x, y, w, h))
    results = out2

    # Trigger: position and width from canonical match (280, 1159 / w=137).
    # Height tuned so the bottom alpha curve meets the bumper alpha curve
    # at their visible edges (canonical has visible trigger bottom ~y=144,
    # visible bumper top ~y=140 — they meet at the rounded seam).
    trigger_overrides_xy_wh = {
        "LeftTrigger":  (280, 0, 137, 144),
        "RightTrigger": (1153, 2, 137, 141),
    }
    # Stick centers from the asset pack diff between
    # "Xbox 360 Controller Overlay (No Thumbstick).png" and the full
    # overlay: L at (296.5, 530.5), R at (997.5, 763.5). Sizes kept
    # at user-approved 185x165 (L) / 180x160 (R) — bbox centered on
    # those diff-derived stick centers.
    stick_overrides = {
        "LeftThumbRing":    (204, 448, 185, 165),
        "RightThumbRing":   (908, 684, 180, 160),
        "LeftThumbButton":  (204, 448, 185, 165),
        "RightThumbButton": (908, 684, 180, 160),
    }
    out = []
    for filename, target, etype, x, y, w, h in results:
        if target in trigger_overrides_xy_wh:
            nx, ny, nw, nh = trigger_overrides_xy_wh[target]
            print(f"  CANONICAL  {target:20s}: ({x},{y}) {w}x{h} -> ({nx},{ny}) {nw}x{nh}  (composite-matched)")
            out.append((filename, target, etype, nx, ny, nw, nh))
        elif target in stick_overrides:
            nx, ny, nw, nh = stick_overrides[target]
            print(f"  CANONICAL  {target:20s}: ({x},{y}) {w}x{h} -> ({nx},{ny}) {nw}x{nh}")
            out.append((filename, target, etype, nx, ny, nw, nh))
        else:
            out.append((filename, target, etype, x, y, w, h))
    return out


def _clip_triggers_above_bumpers(results):
    """Mutate LeftTrigger / RightTrigger entries so their bottom edge sits
    one pixel above the matching shoulder's top edge."""
    bumper_top = {"LeftShoulder": None, "RightShoulder": None}
    for filename, target, etype, x, y, w, h in results:
        if target in bumper_top:
            bumper_top[target] = y
    pair = {"LeftTrigger": "LeftShoulder", "RightTrigger": "RightShoulder"}
    out = []
    for filename, target, etype, x, y, w, h in results:
        if target in pair and bumper_top[pair[target]] is not None:
            # End the trigger exactly at the bumper's top edge (flush) — no
            # 1 px gap. The asset pack's trigger PNG has internal padding,
            # so a flush layout means the visible trigger highlight just
            # meets the bumper edge without a visible seam.
            limit = bumper_top[pair[target]]
            if y + h > limit:
                new_h = max(1, limit - y)
                if new_h != h:
                    print(f"  CLIP-TRIG  {target:20s}: ({x},{y}) {w}x{h} -> {w}x{new_h}  (flush to {pair[target]} top {limit})")
                out.append((filename, target, etype, x, y, w, new_h))
                continue
        out.append((filename, target, etype, x, y, w, h))
    return out


def _xbox360_align_back_start_to_dark_spots(base_path, results):
    """Snap Back / Start positions to the centroids of their drawn dark
    label silhouettes in the Xbox 360 base PNG. Each label is a tiny dark
    oval on the white body between the analog sticks and the guide; find
    it by searching the dark-pixel components within ~80 px of the layout
    entry's current centroid (so we don't pick up handle outlines / charger
    holes that sit at similar y values closer to the controller edges)."""
    base = cv2.imread(base_path, cv2.IMREAD_UNCHANGED)
    if base is None: return results
    gray = cv2.cvtColor(base[:, :, :3], cv2.COLOR_BGR2GRAY)
    dark = (gray < 80).astype(np.uint8)
    n, _, stats, centroids = cv2.connectedComponentsWithStats(dark, connectivity=8)

    refined = []
    for filename, target, etype, x, y, w, h in results:
        if target not in ("ButtonBack", "ButtonStart"):
            refined.append((filename, target, etype, x, y, w, h))
            continue
        ref_cx = x + w / 2.0
        ref_cy = y + h / 2.0
        best = None
        for li in range(1, n):
            bx, by, bw, bh, area = stats[li]
            if not (80 < area < 4000 and 20 < bw < 120 and 15 < bh < 100):
                continue
            cx, cy = float(centroids[li][0]), float(centroids[li][1])
            d = ((cx - ref_cx) ** 2 + (cy - ref_cy) ** 2) ** 0.5
            if d > 80: continue
            if best is None or area > best[2]:
                best = (cx, cy, area)
        if best is None:
            refined.append((filename, target, etype, x, y, w, h))
            continue
        cx, cy, _ = best
        new_x = round(cx - w / 2.0)
        new_y = round(cy - h / 2.0)
        if (new_x, new_y) != (x, y):
            print(f"  DARK-SPOT  {target:20s}: ({x},{y}) -> ({new_x},{new_y}) centroid=({cx:.0f},{cy:.0f})")
        refined.append((filename, target, etype, new_x, new_y, w, h))
    return refined


def process_ds4():
    """Extract DS4 overlay positions."""
    svg_path = os.path.join(ASSET_PACK,
        "DualShock 4 Controller Images", "Default Theme", "Theme SVG",
        "DS4 V2 VSC SVG.svg")

    tree = etree.parse(svg_path)
    root = tree.getroot()

    # DS4 SVG: pt units, 68.423401 DPI
    scale = 68.423401 / 72.0  # pt to pixels

    base = cv2.imread(os.path.join(MODELS_DIR, "DS4", "DS4_V2_base.png"), cv2.IMREAD_UNCHANGED)
    ov_dir = os.path.join(MODELS_DIR, "DS4")

    results = []

    def add(svg_label, filename, target, elem_type):
        bbox = get_element_pixel_bbox(root, svg_label, scale)
        if bbox is None:
            print(f"  MISS: {svg_label}")
            return bbox
        overlay_path = os.path.join(ov_dir, filename)
        pos = center_overlay_on_bbox(bbox, overlay_path)
        results.append((filename, target, elem_type, pos[0], pos[1], pos[2], pos[3]))
        print(f"  {target:20s} ({svg_label:20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")
        return bbox

    print("Parsing DS4 V2 SVG elements...")

    # Face buttons — same overlay image at each button's individual position (diamond layout)
    add("Cross", "DS4_Face_Button.png", "ButtonA", "Button")
    add("Circle", "DS4_Face_Button.png", "ButtonB", "Button")
    add("Square", "DS4_Face_Button.png", "ButtonX", "Button")
    add("Triangle", "DS4_Face_Button.png", "ButtonY", "Button")

    # D-Pad
    add("D-PAD Up", "DS4_D-PAD_Up.png", "DPadUp", "Button")
    add("D-PAD Down", "DS4_D-PAD_Down.png", "DPadDown", "Button")
    add("D-PAD Left", "DS4_D-PAD_Left.png", "DPadLeft", "Button")
    add("D-PAD Right", "DS4_D-PAD_Right.png", "DPadRight", "Button")

    # Bumpers
    add("L1", "DS4_L1-Active.png", "LeftShoulder", "Button")
    add("R1", "DS4_R1-Active.png", "RightShoulder", "Button")

    # Triggers
    add("Left Trigger", "DS4_L2-Active.png", "LeftTrigger", "Trigger")
    add("Right Trigger", "DS4_R2-Active.png", "RightTrigger", "Trigger")

    # Share/Options
    add("Share Button", "DS4_OptionsShare_Button.png", "ButtonBack", "Button")
    add("Option Button", "DS4_OptionsShare_Button.png", "ButtonStart", "Button")

    # PS/Guide button
    add("PS Button", "DS4_Home_Button.png", "ButtonGuide", "Button")

    # Sticks
    add("Left Stick", "DS4_V2_LeftAnalogStick.png", "LeftThumbRing", "StickRing")
    add("Right Stick", "DS4_V2_RightAnalogStick.png", "RightThumbRing", "StickRing")

    # Stick clicks — same position as sticks
    left_bbox = get_element_pixel_bbox(root, "Left Stick", scale)
    right_bbox = get_element_pixel_bbox(root, "Right Stick", scale)
    if left_bbox:
        pos = center_overlay_on_bbox(left_bbox, os.path.join(ov_dir, "DS4_AnalogStick_Click.png"))
        results.append(("DS4_AnalogStick_Click.png", "LeftThumbButton", "StickClick", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {'LeftThumbButton':20s} ({'Left Stick':20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")
    if right_bbox:
        pos = center_overlay_on_bbox(right_bbox, os.path.join(ov_dir, "DS4_AnalogStick_Click.png"))
        results.append(("DS4_AnalogStick_Click.png", "RightThumbButton", "StickClick", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {'RightThumbButton':20s} ({'Right Stick':20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")

    # Refine positions using full composite overlay
    composite_path = os.path.join(ov_dir, "DualShock 4 Controller V2 Model Overlay.png")
    print("\nRefining DS4 positions via alpha-channel template matching...")
    results = refine_with_composite(composite_path, results)

    base_w, base_h = base.shape[1], base.shape[0]

    # DS4 post-pass overrides:
    # - Pull each d-pad cardinal 20 px toward the d-pad center (so the
    #   four arrows meet in the middle like a continuous + cross
    #   instead of four detached buttons).
    # - Shrink stick ring/click to 150x145 / 165x160 — the SVG bbox
    #   yields 165/196, which extends past the visible stick well.
    # - Lower Share/Options by 20 px — the SVG label sits on the text
    #   ("SHARE" / "OPTIONS"), not on the actual button bump below it.
    # - Add Touchpad + TouchpadClick zones (was lost during the v3 SVG
    #   rewrite — DS4 had a manual zone in v2).
    results = _ds4_post_pass(results, base_w, base_h)

    return {"base_width": base_w, "base_height": base_h, "results": results}


def _ds4_post_pass(results, base_w, base_h):
    # D-pad: pull all four cardinals inward toward the d-pad center.
    # Up/Down need a smaller pull-in than Left/Right (the up/down
    # arrows are taller than they are wide; an aggressive pull
    # collapses them onto each other). All four scaled 0.93 to fit
    # their silhouettes more tightly.
    DPAD_LR_INWARD = 15
    DPAD_UD_INWARD = 10
    DPAD_SCALE = 0.93
    # Share / Options: drop 15 px so the highlight lands on the actual
    # button bump (the SVG label sits on the "SHARE" / "OPTIONS" text
    # above the bump).
    SHARE_OPTIONS_DROP = 15
    # Sticks: ThumbRing is the gray thumbstick face (DS4_V2_*AnalogStick
    # .png) — NOT a blue overlay. Keep at native size and SVG position.
    # ThumbButton is the BLUE click highlight (DS4_AnalogStick_Click.png)
    # — narrow/shorten to match the thumbstick face. Bottom anchored at
    # original y=675; width/height reduced symmetrically.
    # Match the rendered thumbstick face (165x147) — that IS the
    # visible target the blue press highlight should sit on.
    # Bottom anchored at original y=675; centers x=509/957.
    STICK_OVERRIDES = {
        "LeftThumbButton":  (427, 528, 165, 147),
        "RightThumbButton": (875, 528, 165, 147),
    }
    # Triggers: positions and size from the asset pack's V2 canonical
    # composite (DualShock 4 Controller V2 Model Overlay.png).
    # Template-match of DS4_L2.png (164x94 native) against the
    # composite locates L2 at (217, 0) and R2 at (1085, 0). The
    # active PNG (134x80 native) gets resized to the same bbox so
    # the press highlight covers the rest-state silhouette exactly.
    TRIGGER_OVERRIDES = {
        "LeftTrigger":  (217, 0, 164, 94),
        "RightTrigger": (1085, 0, 164, 94),
    }
    out = []
    for filename, target, etype, x, y, w, h in results:
        if target in ("DPadUp", "DPadDown", "DPadLeft", "DPadRight"):
            cx, cy = x + w / 2, y + h / 2
            nw, nh = int(w * DPAD_SCALE), int(h * DPAD_SCALE)
            if target == "DPadUp":    cy += DPAD_UD_INWARD
            if target == "DPadDown":  cy -= DPAD_UD_INWARD
            if target == "DPadLeft":  cx += DPAD_LR_INWARD
            if target == "DPadRight": cx -= DPAD_LR_INWARD
            nx, ny = round(cx - nw / 2), round(cy - nh / 2)
            out.append((filename, target, etype, nx, ny, nw, nh))
        elif target in ("ButtonBack", "ButtonStart"):
            out.append((filename, target, etype, x, y + SHARE_OPTIONS_DROP, w, h))
        elif target in STICK_OVERRIDES:
            nx, ny, nw, nh = STICK_OVERRIDES[target]
            out.append((filename, target, etype, nx, ny, nw, nh))
        elif target in TRIGGER_OVERRIDES:
            nx, ny, nw, nh = TRIGGER_OVERRIDES[target]
            out.append((filename, target, etype, nx, ny, nw, nh))
        else:
            out.append((filename, target, etype, x, y, w, h))

    # TouchpadClick = click highlight PNG bounds. Sized to native PNG
    # (482x289). Visually tuned position so PNG traces the touchpad
    # outline.
    # Touchpad = the actual touchpad surface area used for finger-dot
    # normalized-coord mapping (smaller — just the visible surface
    # between SHARE and OPTIONS, not including the PNG's outer border).
    out.append(("", "TouchpadClick", "Button",   492, 148, 482, 289))
    out.append(("", "Touchpad",      "Touchpad", 496, 230, 471, 200))
    print(f"  TouchpadClick        (PNG visual)          -> (492, 148) 482x289")
    print(f"  Touchpad             (finger zone)         -> (496, 230) 471x200")
    return out


def process_dualsense():
    """Extract DualSense overlay positions. SVG units = mm; default theme PNG
    is 1467x816 → scale ≈ 2.6932 px/mm. Touchpad-click and touchpad zones are
    injected manually since the SVG doesn't label them."""
    svg_path = os.path.join(ASSET_PACK,
        "DualSense Controller Image", "Default", "Theme SVG",
        "DualSense VSCView SVG.svg")

    tree = etree.parse(svg_path)
    root = tree.getroot()

    base = cv2.imread(os.path.join(MODELS_DIR, "DualSense", "DualSense_base.png"), cv2.IMREAD_UNCHANGED)
    base_w, base_h = base.shape[1], base.shape[0]

    # SVG declares 544.7066 mm width; PNG is 1467 px → 2.6932 px/mm.
    scale = base_w / 544.7066

    ov_dir = os.path.join(MODELS_DIR, "DualSense")
    results = []

    def add(svg_label, filename, target, elem_type, fit_scale=1.0):
        """Resize the press-overlay PNG to fit the SVG label's bbox so size
        and position both come from visual analysis of the SVG silhouette,
        not from whatever scale the asset-pack author chose for the PNG."""
        bbox = get_element_pixel_bbox(root, svg_label, scale)
        if bbox is None:
            print(f"  MISS: {svg_label}")
            return None
        overlay_path = os.path.join(ov_dir, filename)
        pos = fit_overlay_to_bbox(bbox, overlay_path, scale=fit_scale)
        results.append((filename, target, elem_type, pos[0], pos[1], pos[2], pos[3]))
        print(f"  {target:20s} ({svg_label:20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")
        return bbox

    print("Parsing DualSense SVG elements...")

    # Face buttons — separate PNG per button (Cross/Circle/Square/Triangle).
    # SVG label "Crosss" has an extra 's' (asset-pack typo); "Triangle " has
    # a trailing space.
    add("Crosss", "DualSense_Cross.png", "ButtonA", "Button")
    add("Circle", "DualSense_Circle.png", "ButtonB", "Button")
    add("Square", "DualSense_Square.png", "ButtonX", "Button")
    add("Triangle ", "DualSense_Triangle.png", "ButtonY", "Button")

    # D-Pad
    add("D-PAD Up", "DualSense_D-PAD_Up.png", "DPadUp", "Button")
    add("D-PAD Down", "DualSense_D-PAD_Down.png", "DPadDown", "Button")
    add("D-PAD Left", "DualSense_D-PAD_Left.png", "DPadLeft", "Button")
    add("D-PAD Right", "DualSense_D-PAD_Right.png", "DPadRight", "Button")

    # Bumpers — SVG labels "L1"/"R1" trace the entire shoulder bumper
    # outline; the bbox center is the right anchor for the bumper PNG.
    add("L1", "DualSense_L1-Active.png", "LeftShoulder", "Button")
    add("R1", "DualSense_R1-Active.png", "RightShoulder", "Button")

    # Triggers (note SVG label typos: "L2 Triggers", "R2 Trigger")
    add("L2 Triggers", "DualSense_L2-Active.png", "LeftTrigger", "Trigger")
    add("R2 Trigger", "DualSense_R2-Active.png", "RightTrigger", "Trigger")

    # Create / Option / PS buttons
    add("Create Button", "DualSense_Create_Button.png", "ButtonBack", "Button")
    add("Option Button", "DualSense_Option_Button.png", "ButtonStart", "Button")
    add("PS Button", "DualSense_Home_Button.png", "ButtonGuide", "Button")

    # Sticks (rings) and stick clicks share the same SVG bbox.
    add("Left Stick", "DualSense_LeftAnalogStick.png", "LeftThumbRing", "StickRing")
    add("Right Stick", "DualSense_RightAnalogStick.png", "RightThumbRing", "StickRing")
    left_bbox = get_element_pixel_bbox(root, "Left Stick", scale)
    right_bbox = get_element_pixel_bbox(root, "Right Stick", scale)
    if left_bbox:
        pos = fit_overlay_to_bbox(left_bbox, os.path.join(ov_dir, "DualSense_AnalogStick_Click.png"))
        results.append(("DualSense_AnalogStick_Click.png", "LeftThumbButton", "StickClick", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {'LeftThumbButton':20s} ({'Left Stick':20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")
    if right_bbox:
        pos = fit_overlay_to_bbox(right_bbox, os.path.join(ov_dir, "DualSense_AnalogStick_Click.png"))
        results.append(("DualSense_AnalogStick_Click.png", "RightThumbButton", "StickClick", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {'RightThumbButton':20s} ({'Right Stick':20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")

    # Refine Create/Option/PS via base alpha template — same situation as
    # Xbox 360's Start/Back: the SVG labels sit on text or icon centroids
    # that are slightly offset from the visible button silhouette.
    base_path = os.path.join(MODELS_DIR, "DualSense", "DualSense_base.png")
    print("Refining DualSense small-button positions via base alpha template...")
    results = refine_via_base_template(base_path, results, ov_dir,
        targets={"ButtonBack", "ButtonStart", "ButtonGuide"})

    # TouchpadClick = click highlight PNG bounds (621x322 native).
    # Touchpad = the actual touchpad surface for finger-dot mapping
    # (smaller area, original v3 layout).
    click_x = round((base_w - 621) / 2)
    results.append(("", "TouchpadClick", "Button",   click_x, 160, 621, 322))
    tp_w_inner = round(base_w * 0.34)
    tp_h_inner = round(base_h * 0.27)
    tp_x_inner = round((base_w - tp_w_inner) / 2)
    tp_y_inner = round(base_h * 0.27)
    results.append(("", "Touchpad", "Touchpad",
                    tp_x_inner, tp_y_inner, tp_w_inner, tp_h_inner))
    print(f"  TouchpadClick        (PNG visual)          -> ({click_x}, 160) 621x322")
    print(f"  Touchpad             (finger zone)         -> ({tp_x_inner}, {tp_y_inner}) {tp_w_inner}x{tp_h_inner}")

    return {"base_width": base_w, "base_height": base_h, "results": results}


def _process_xbox_modern(profile_name, svg_path, base_relpath, ov_subdir,
                        composite_filename, prefix,
                        face_btn_filenames, dpad_filenames,
                        bumper_filenames, trigger_filenames,
                        stick_filenames, stick_click_filenames,
                        guide_filename, menu_filename, view_filename,
                        share_filename=None,
                        bumper_width_frac=0.202,
                        dpad_fit_scale=1.0):
    """Shared driver for Xbox One and Xbox Series X SVGs. Both have viewBox
    units that map 1:1 to PNG pixels, similar SVG label conventions, and
    the same press-overlay shape (face buttons + sticks have individual
    labels; bumpers + d-pad need bbox splitting / quadrant computation)."""
    tree = etree.parse(svg_path)
    root = tree.getroot()

    base = cv2.imread(os.path.join(MODELS_DIR, ov_subdir, os.path.basename(base_relpath)), cv2.IMREAD_UNCHANGED)
    base_w, base_h = base.shape[1], base.shape[0]

    # viewBox-units already match PNG pixel coordinates closely; scale = 1.
    scale = 1.0
    ov_dir = os.path.join(MODELS_DIR, ov_subdir)
    results = []

    def add(svg_label, filename, target, elem_type, fit_scale=1.0):
        """Place the press-overlay PNG sized to the SVG label's bbox.
        Resizes the PNG on disk so the layout entry's width/height match
        the actual button region drawn on the controller silhouette."""
        bbox = get_element_pixel_bbox(root, svg_label, scale)
        if bbox is None:
            print(f"  MISS: {svg_label}")
            return None
        overlay_path = os.path.join(ov_dir, filename)
        pos = fit_overlay_to_bbox(bbox, overlay_path, scale=fit_scale)
        results.append((filename, target, elem_type, pos[0], pos[1], pos[2], pos[3]))
        print(f"  {target:20s} ({svg_label:20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")
        return bbox

    print(f"Parsing {profile_name} SVG elements...")

    # Face buttons (individual labels in both SVGs). Sized to the SVG
    # element's own bbox; PNG is resized on disk to match.
    add("A Button", face_btn_filenames["A"], "ButtonA", "Button")
    add("B Button", face_btn_filenames["B"], "ButtonB", "Button")
    add("X Button", face_btn_filenames["X"], "ButtonX", "Button")
    add("Y Button", face_btn_filenames["Y"], "ButtonY", "Button")

    # Bumpers — the SVG only labels the bumper PAIR as one group; that
    # group's bbox spans the wide empty zone between the two bumpers, so
    # fitting a PNG to half the group oversizes it ~2x. The Xbox 360 layout
    # (which renders correctly) ships its bumper PNG at ~20% of base width;
    # use the same target width for Xbox One/Series and pin each PNG to the
    # corresponding outer edge of the bumper group bbox. PNG aspect ratio is
    # preserved so the highlight shape stays correct.
    bumper_label = bumper_filenames["GroupLabel"]
    bumper_bbox = get_element_pixel_bbox(root, bumper_label, scale)
    if bumper_bbox:
        bx, by, bw, bh = bumper_bbox
        target_bumper_w = int(round(base_w * bumper_width_frac))
        for side, fn, target in [("L", bumper_filenames["L"], "LeftShoulder"),
                                 ("R", bumper_filenames["R"], "RightShoulder")]:
            overlay_path = os.path.join(ov_dir, fn)
            ov = cv2.imread(overlay_path, cv2.IMREAD_UNCHANGED)
            if ov is None: continue
            target_h = max(1, int(round(target_bumper_w * ov.shape[0] / ov.shape[1])))
            scaled = cv2.resize(ov, (target_bumper_w, target_h), interpolation=cv2.INTER_LANCZOS4)
            cv2.imwrite(overlay_path, scaled)
            x = round(bx) if side == "L" else round(bx + bw - target_bumper_w)
            y = round(by + (bh - target_h) / 2)
            results.append((fn, target, "Button", x, y, target_bumper_w, target_h))
            print(f"  {target:20s} ({bumper_label} edge {side}) -> ({x:4d}, {y:4d}) {target_bumper_w:4d}x{target_h:3d}")

    # Triggers
    add(trigger_filenames["LLabel"], trigger_filenames["L"], "LeftTrigger", "Trigger")
    add(trigger_filenames["RLabel"], trigger_filenames["R"], "RightTrigger", "Trigger")

    # System buttons — Xbox Series adds Share; both have Menu / View / Guide.
    add("Menu Button", menu_filename, "ButtonStart", "Button")
    add("View Button", view_filename, "ButtonBack", "Button")
    # Guide button — group bbox includes hub LEDs; prefer the "Xbox Button"
    # inner label (= the Xbox-logo button itself, no LED ring) if present,
    # else fall back to the full guide group.
    guide_bbox = get_element_pixel_bbox(root, "Xbox Button", scale)
    if guide_bbox is None:
        guide_bbox = get_element_pixel_bbox(root, "Xbox Guide Button", scale)
    if guide_bbox:
        pos = fit_overlay_to_bbox(guide_bbox, os.path.join(ov_dir, guide_filename))
        results.append((guide_filename, "ButtonGuide", "Button", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {'ButtonGuide':20s} ({'Xbox Button/Guide':20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")

    # Sticks
    add("Left Stick", stick_filenames["L"], "LeftThumbRing", "StickRing")
    add("Right Stick", stick_filenames["R"], "RightThumbRing", "StickRing")
    left_bbox = get_element_pixel_bbox(root, "Left Stick", scale)
    right_bbox = get_element_pixel_bbox(root, "Right Stick", scale)
    if left_bbox:
        # Per-stick click art. This used to take ONE filename for both, so
        # the right thumb button rendered the LEFT stick's click PNG at the
        # right stick's position. A pair, like stick_filenames alongside it.
        pos = fit_overlay_to_bbox(left_bbox, os.path.join(ov_dir, stick_click_filenames[0]))
        results.append((stick_click_filenames[0], "LeftThumbButton", "StickClick", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {'LeftThumbButton':20s} ({'Left Stick':20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")
    if right_bbox:
        pos = fit_overlay_to_bbox(right_bbox, os.path.join(ov_dir, stick_click_filenames[1]))
        results.append((stick_click_filenames[1], "RightThumbButton", "StickClick", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {'RightThumbButton':20s} ({'Right Stick':20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")

    # D-PAD — same group-split approach as bumpers, but four quadrants.
    dpad_bbox = None
    for label in dpad_filenames["GroupLabels"]:
        dpad_bbox = get_element_pixel_bbox(root, label, scale)
        if dpad_bbox:
            print(f"  D-PAD using group label: {label}")
            break
    if dpad_bbox:
        dx, dy, dw, dh = dpad_bbox
        half_w, half_h = dw / 2.0, dh / 2.0
        # Up/Down/Left/Right overlays should sit AT the outer edge of the
        # d-pad bbox, not centered in their half. fit_overlay_to_bbox
        # centers within the supplied sub-rect, so we shape each sub-rect
        # so its center IS the desired anchor point: outer-edge-of-half
        # (vs middle-of-half). Combined with dpad_fit_scale<1 this places
        # each press overlay flush with its arm tip rather than floating
        # in the middle of the arm.
        for direction, fn, target, sub in [
            # Up: sub-rect occupies upper portion only — top edge anchored,
            # height = scaled overlay height, so fit centers on the rim.
            ("Up",    dpad_filenames["Up"],    "DPadUp",    (dx,                    dy,                     dw,                  half_h * dpad_fit_scale)),
            ("Down",  dpad_filenames["Down"],  "DPadDown",  (dx,                    dy + dh - half_h*dpad_fit_scale, dw,         half_h * dpad_fit_scale)),
            ("Left",  dpad_filenames["Left"],  "DPadLeft",  (dx,                    dy,                     half_w * dpad_fit_scale, dh)),
            ("Right", dpad_filenames["Right"], "DPadRight", (dx + dw - half_w*dpad_fit_scale, dy,           half_w * dpad_fit_scale, dh)),
        ]:
            pos = fit_overlay_to_bbox(sub, os.path.join(ov_dir, fn))
            results.append((fn, target, "Button", pos[0], pos[1], pos[2], pos[3]))
            print(f"  {target:20s} ({'D-PAD '+direction:20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")

    # Refine bumper positions against the base PNG. The SVG bumper-group
    # bbox spans the empty middle between the two bumpers, so simply
    # pinning to its outer edges puts each bumper at a coarse position;
    # template-matching the bumper highlight shape against the base
    # locates the actual visible bumper on the controller silhouette.
    base_path = os.path.join(MODELS_DIR, ov_subdir, os.path.basename(base_relpath))
    print(f"Refining {profile_name} bumper positions via base alpha template...")
    results = refine_via_base_template(base_path, results, ov_dir,
        targets={"LeftShoulder", "RightShoulder"}, search_radius=120)

    return {"base_width": base_w, "base_height": base_h, "results": results}


def process_xbox_one_s():
    """Extract Xbox One S overlay positions."""
    svg_path = os.path.join(ASSET_PACK,
        "Xbox Wireless Controller Images", "Default Theme", "Theme SVG",
        "Xbox One Color", "Xbox One Controller VSCView White.svg")
    data = _process_xbox_modern(
        profile_name="Xbox One S",
        svg_path=svg_path,
        base_relpath="2DModels/XBOXONE/XB1_S_base.png",
        ov_subdir="XBOXONE",
        composite_filename="Xbox One S Controller Overlay.png",
        prefix="XB1",
        face_btn_filenames={"A": "XB1_A_Button.png", "B": "XB1_B_Button.png",
                            "X": "XB1_X_Button.png", "Y": "XB1_Y_Button.png"},
        dpad_filenames={"GroupLabels": ["D-PAD"],
                        "Up": "XB1_D-PAD_Up.png", "Down": "XB1_D-PAD_Down.png",
                        "Left": "XB1_D-PAD_Left.png", "Right": "XB1_D-PAD_Right.png"},
        bumper_filenames={"GroupLabel": "Xbox One Bumpers",
                          "L": "XB1_LeftBumper_Active.png",
                          "R": "XB1_RightBumper_Active.png"},
        trigger_filenames={"L": "XB1_LeftTrigger_Active.png", "LLabel": "Left Trigger",
                           "R": "XB1_RightTrigger_Active.png", "RLabel": "Right Triggers"},
        stick_filenames={"L": "XB1_LeftStick.png", "R": "XB1_RightStick.png"},
        stick_click_filenames=("XB1_LeftStick_Click.png", "XB1_RightStick_Click.png"),
        guide_filename="XB1_HomeButton.png",
        menu_filename="XB1_MenuButton.png",
        view_filename="XB1_ViewButton.png",
        # Xbox One bumpers wrap further than the Xbox 360 reference 0.202.
        # Visual rendering compared 0.235/0.240/0.245/0.250/0.270 — 0.245
        # covers the bumper silhouette without overshooting into the trigger
        # well. PNG aspect ratio is preserved so the height grows
        # proportionally.
        bumper_width_frac=0.245)

    # Shift bumpers outward (5 px each) so their outer edges align with
    # the controller body silhouette. The base-template refine lands a
    # few pixels short of the body edge because the bumper highlight has
    # a soft alpha falloff that biases the match inward.
    data["results"] = _shift_bumpers_outward(data["results"], shift=5)
    # Stretch the left blue click overlay to match the left thumbstick
    # height (composite-refine lands the click ~10 px shorter on Xbox
    # One S; user wants them flush).
    data["results"] = _match_left_click_to_ring(data["results"])
    # Trigger positions/sizes from canonical composite template-match.
    data["results"] = _override_triggers(data["results"], {
        "LeftTrigger":  (188, 1, 206, 188),
        "RightTrigger": (1150, 0, 208, 189),
    })
    return data


def _override_triggers(results, overrides):
    out = []
    for filename, target, etype, x, y, w, h in results:
        if target in overrides:
            nx, ny, nw, nh = overrides[target]
            out.append((filename, target, etype, nx, ny, nw, nh))
        else:
            out.append((filename, target, etype, x, y, w, h))
    return out


def _shift_bumpers_outward(results, shift):
    out = []
    for filename, target, etype, x, y, w, h in results:
        if target == "LeftShoulder":
            out.append((filename, target, etype, x - shift, y, w, h))
            print(f"  SHIFT-OUT  {target:20s}: ({x},{y}) -> ({x-shift},{y})")
        elif target == "RightShoulder":
            out.append((filename, target, etype, x + shift, y, w, h))
            print(f"  SHIFT-OUT  {target:20s}: ({x},{y}) -> ({x+shift},{y})")
        else:
            out.append((filename, target, etype, x, y, w, h))
    return out


def _match_left_click_to_ring(results):
    """Match LeftThumbButton (blue click overlay) height/y to LeftThumbRing
    (the gray thumbstick) so the highlight covers the full thumbstick face."""
    ring = next((r for r in results if r[1] == "LeftThumbRing"), None)
    if ring is None:
        return results
    _, _, _, _, ring_y, _, ring_h = ring
    out = []
    for filename, target, etype, x, y, w, h in results:
        if target == "LeftThumbButton":
            print(f"  MATCH-RING {target:20s}: ({x},{y}) {w}x{h} -> ({x},{ring_y}) {w}x{ring_h}")
            out.append((filename, target, etype, x, ring_y, w, ring_h))
        else:
            out.append((filename, target, etype, x, y, w, h))
    return out


def process_xbox_series():
    """Extract Xbox Series X overlay positions."""
    svg_path = os.path.join(ASSET_PACK,
        "Xbox Wireless Controller Images", "Default Theme", "Theme SVG",
        "Xbox Series X Color", "Xbox Series X Controller VSCView White.svg")
    data = _process_xbox_modern(
        profile_name="Xbox Series X",
        svg_path=svg_path,
        base_relpath="2DModels/XBOXSERIES/XBSeries_base.png",
        ov_subdir="XBOXSERIES",
        composite_filename="Xbox Series X Controller Overlay.png",
        prefix="XBSeries",
        face_btn_filenames={"A": "XBSeries_A_Button.png", "B": "XBSeries_B_Button.png",
                            "X": "XBSeries_X_Button.png", "Y": "XBSeries_Y_Button.png"},
        dpad_filenames={"GroupLabels": ["Main D-PAD", "Xbox Series Controller D-PAD", "Front D-PAD"],
                        "Up": "XBSeries_D-PAD_Up.png", "Down": "XBSeries_D-PAD_Down.png",
                        "Left": "XBSeries_D-PAD_Left.png", "Right": "XBSeries_D-PAD_Right.png"},
        bumper_filenames={"GroupLabel": "Bumpers",
                          "L": "XBSeries_LeftBumper_Active.png",
                          "R": "XBSeries_RightBumper_Active.png"},
        trigger_filenames={"L": "XBSeries_LeftTrigger_Active.png", "LLabel": "Left Trigger",
                           "R": "XBSeries_RightTrigger_Active.png", "RLabel": "Right Trigger"},
        stick_filenames={"L": "XBSeries_LeftStick.png", "R": "XBSeries_RightStick.png"},
        stick_click_filenames=("XBSeries_LeftStick_Click.png", "XBSeries_RightStick_Click.png"),
        guide_filename="XBSeries_HomeButton.png",
        menu_filename="XBSeries_MenuButton.png",
        view_filename="XBSeries_ViewButton.png",
        # Xbox Series bumpers wrap further around the controller's top
        # corners than Xbox One/360 — bump the width target to match.
        bumper_width_frac=0.235,
        # The "Main D-PAD" SVG group bbox spans the full hybrid disc on
        # Series; halving it leaves each direction overlay covering ~half
        # the disc, which reads as too big over the smaller visible button.
        # Shrink each quadrant fit by 30% so the overlay fits the visible
        # arrow rather than the entire arm.
        dpad_fit_scale=0.7)
    # Xbox Series has a dedicated Share button between Menu and View.
    root = etree.parse(os.path.join(ASSET_PACK,
        "Xbox Wireless Controller Images", "Default Theme", "Theme SVG",
        "Xbox Series X Color", "Xbox Series X Controller VSCView White.svg")).getroot()
    share_bbox = get_element_pixel_bbox(root, "Share Button", 1.0)
    if share_bbox:
        ov_dir = os.path.join(MODELS_DIR, "XBOXSERIES")
        pos = fit_overlay_to_bbox(share_bbox, os.path.join(ov_dir, "XBSeries_ShareButton.png"))
        data["results"].append(("XBSeries_ShareButton.png", "ButtonShare", "Button", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {'ButtonShare':20s} ({'Share Button':20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")
    # Match the left blue click overlay to the left thumbstick height.
    data["results"] = _match_left_click_to_ring(data["results"])
    return data


SWPRO_BODY_W = 1485    # the pack render's own width, what the SVG maps onto
SWPRO_MARGIN = 160     # Switch 2 Pro only: gutter each side for the GL / GR tiles
SWPRO_TILE = 130       # GL / GR tile edge
SWPRO_TILE_Y = 735     # tile top, level with the grips


def process_switchpro():
    """Original Nintendo Switch Pro Controller. No gutter and none of the
    Switch 2 controls: this set is exactly the pack's own art, because a
    switch-pro slot must not render a C button or grip tiles it has no
    wire for."""
    return _process_switchpro_family("SWITCHPRO", margin=0, switch2=False)


def process_switch2pro():
    """Nintendo Switch 2 Pro Controller. Its OWN asset folder: a copy of
    the same pack sprites over a base widened by a side gutter, carrying
    the three controls the original does not have (C on the face, GL / GR
    as floating tiles)."""
    return _process_switchpro_family("SWITCH2PRO", margin=SWPRO_MARGIN, switch2=True)


def _process_switchpro_family(folder, margin, switch2):
    """Extract Nintendo Switch Pro Controller overlay positions.

    This pack's press overlays are authored ~1.55x oversized relative to
    the base render (content == canvas, no padding), so every element is
    fit_overlay_to_bbox-sized to its SVG label's bbox, the same posture
    as Xbox One / Series. The SVG labels sit ON the controls (unlike
    DS4's text-anchored labels), so the fitted geometry is authoritative
    and no composite refinement pass is needed. ZL/ZR keep the Trigger
    typing (the digital preview bridge drives the fill 0/1, so a press
    shows the full highlight); their rest art lives in the base render,
    so no TriggerBase pair is needed.
    """
    svg_path = os.path.join(ASSET_PACK,
        "Nintendo Switch Controller Images", "Switch Pro Controller",
        "Default Theme", "Theme SVG", "Switch Pro Controller VSCView.svg")

    tree = etree.parse(svg_path)
    root = tree.getroot()

    base = cv2.imread(os.path.join(MODELS_DIR, folder, "NSwitchPro_base.png"), cv2.IMREAD_UNCHANGED)
    base_w, base_h = base.shape[1], base.shape[0]
    if base_w != SWPRO_BODY_W + 2 * margin:
        raise SystemExit(f"{folder} base is {base_w}px wide; expected "
                         f"{SWPRO_BODY_W} + 2x{margin} gutter")

    # SVG is authored in mm (viewBox 0 0 419.127 304.546); px-per-mm from
    # the width ratio against the pack render's own 1485x1079 body. NOT
    # against base_w: the Switch 2 base carries a gutter the SVG knows
    # nothing about, so scaling by the full canvas width would stretch
    # every label bbox by the gutter ratio.
    vb = [float(v) for v in root.get("viewBox").split()]
    scale = SWPRO_BODY_W / vb[2]

    ov_dir = os.path.join(MODELS_DIR, folder)
    results = []

    def add(svg_label, filename, target, elem_type, fit_scale=1.0):
        bbox = get_element_pixel_bbox(root, svg_label, scale)
        if bbox is None:
            print(f"  MISS: {svg_label}")
            return None
        pos = fit_overlay_to_bbox(bbox, os.path.join(ov_dir, filename), scale=fit_scale)
        results.append((filename, target, elem_type, pos[0], pos[1], pos[2], pos[3]))
        print(f"  {target:20s} ({svg_label:20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")
        return bbox

    print("Parsing Switch Pro SVG elements...")

    # Face buttons: one shared cap overlay fitted to each lettered
    # position. Nintendo layout (A east, B south, X north, Y west);
    # targets follow the LETTERS so the highlight matches the cap.
    add("A Button", "NSwitchPro_FaceButton.png", "ButtonA", "Button")
    add("B Button", "NSwitchPro_FaceButton.png", "ButtonB", "Button")
    add("X Button", "NSwitchPro_FaceButton.png", "ButtonX", "Button")
    add("Y Button", "NSwitchPro_FaceButton.png", "ButtonY", "Button")

    # D-pad: only the full cross group is labeled. Same edge-anchored
    # quadrant sub-rects as the Xbox One / Series flow.
    dpad_bbox = get_element_pixel_bbox(root, "D-PAD", scale)
    if dpad_bbox:
        dx, dy, dw, dh = dpad_bbox
        half_w, half_h = dw / 2.0, dh / 2.0
        for direction, fn, target, sub in [
            ("Up",    "NSwitchPro_D-PAD_Up.png",    "DPadUp",    (dx, dy, dw, half_h)),
            ("Down",  "NSwitchPro_D-PAD_Down.png",  "DPadDown",  (dx, dy + dh - half_h, dw, half_h)),
            ("Left",  "NSwitchPro_D-PAD_Left.png",  "DPadLeft",  (dx, dy, half_w, dh)),
            ("Right", "NSwitchPro_D-PAD_Right.png", "DPadRight", (dx + dw - half_w, dy, half_w, dh)),
        ]:
            pos = fit_overlay_to_bbox(sub, os.path.join(ov_dir, fn))
            results.append((fn, target, "Button", pos[0], pos[1], pos[2], pos[3]))
            print(f"  {target:20s} ({'D-PAD ' + direction:20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")

    # Digital triggers BEFORE bumpers, deliberately: the ZL/ZR SVG
    # bboxes run ~60 px down behind the bumper wings (most of the nub
    # is hidden), and the view's hover/click rectangles resolve to the
    # LAST-added overlay in an overlap. Bumpers added after the
    # triggers win the shared band, so a cursor below the visible
    # trigger arc highlights the bumper, and the trigger only answers
    # above the bumper's top edge where its arc actually shows. This
    # also matches the physical stacking (bumper in front of trigger).
    add("ZL Trigger", "NSwitchPro_ZL.png", "LeftTrigger", "Trigger")
    add("ZR Trigger", "NSwitchPro_ZR.png", "RightTrigger", "Trigger")

    # Rest-state trigger silhouettes, the Xbox TriggerBase pattern: the
    # shipped base render is the pack's trigger-LESS template variant,
    # so without these the ZL/ZR nubs don't exist at rest and a press
    # lights a floating arc. The art is the pixel difference between
    # the pack's with-triggers and without-triggers templates (see
    # tools note in the repo history), cropped at base-canvas
    # registration, so position is exact and no fitting applies. The
    # view renders TriggerBase at Z 0, BEHIND the body render, so the
    # silhouette peeks above the shoulder line exactly like Xbox / DS4.
    results.append(("NSwitchPro_ZL_Rest.png", "LeftTriggerBase", "TriggerBase", 200, 0, 278, 100))
    print(f"  {'LeftTriggerBase':20s} ({'template diff':20s}) -> ( 200,    0)  278x100")
    results.append(("NSwitchPro_ZR_Rest.png", "RightTriggerBase", "TriggerBase", 1007, 0, 276, 100))
    print(f"  {'RightTriggerBase':20s} ({'template diff':20s}) -> (1007,    0)  276x100")
    add("L Bumper", "NSwitchPro_L_Bumper.png", "LeftShoulder", "Button")
    add("R Bumper", "NSwitchPro_R_Bumper.png", "RightShoulder", "Button")

    # System cluster: Minus/Plus share one overlay; Home and Capture own.
    add("Minus", "NSwitchPro_Plus-MinusButton.png", "ButtonBack", "Button")
    add("Plus", "NSwitchPro_Plus-MinusButton.png", "ButtonStart", "Button")
    add("Home", "NSwitchPro_HomeButton.png", "ButtonGuide", "Button")
    add("Capture", "NSwitchPro_CaptureButton.png", "ButtonShare", "Button")

    # Sticks: the face images are authored at base scale and the fit is
    # a no-op; the click highlight is oversized and fits down to the
    # stick well like the DS4 post-pass clamps its click overlay.
    add("Left Joystick", "NSwitchPro_LeftStick.png", "LeftThumbRing", "StickRing")
    add("Right Joystick", "NSwitchPro_RightStick.png", "RightThumbRing", "StickRing")
    for lbl, target in [("Left Joystick", "LeftThumbButton"),
                        ("Right Joystick", "RightThumbButton")]:
        bbox = get_element_pixel_bbox(root, lbl, scale)
        if bbox:
            pos = fit_overlay_to_bbox(bbox, os.path.join(ov_dir, "NSwitchPro_AnalogStickClick.png"))
            results.append(("NSwitchPro_AnalogStickClick.png", target, "StickClick", pos[0], pos[1], pos[2], pos[3]))
            print(f"  {target:20s} ({lbl:20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")

    if switch2:
        # No SVG labels exist for these: the pack's theme SVG is the
        # ORIGINAL Pro Controller's. Derived instead from the purchased
        # hado Switch 2 Pro mesh in 3DModels/Switch2Pro, mapped into this
        # frame by a linear fit over the controls both pads carry, which
        # agrees to a few px on the right half and ~1 px at the D-pad.
        #
        # C Button: mesh centroid x=0.00 (dead centre) z=-12.08 (below both
        # the D-pad and the right stick) -> (742.5, 656) body-frame, and
        # Nintendo's own controller diagram places it "on the front face
        # between the D-pad and right stick area". It reuses the Capture
        # press sprite because the mesh gives the two an identical 6.28mm
        # footprint and the same rounded-square corner profile (top-face
        # radius spread 1.35 vs 1.32; the round face buttons measure 1.00).
        # Fixed rect, no fit pass: fit_overlay_to_bbox rewrites the PNG on
        # disk, and this sprite is already sized by its own Capture entry.
        #
        # Target names are the preview grammar the 2D/3D art and the raw
        # bridge share. NintendoPreviewMap maps the switch2-pro wire onto
        # exactly these: GR b18, GL b19, C b20.
        results.append(("NSwitchPro_CaptureButton.png", "ButtonC", "Button", 712, 624, 62, 63))
        print(f"  {'ButtonC':20s} ({'C, from S2 mesh':20s}) -> ( 712,  624)   62x 63")

    # Shift the body-frame results into the widened canvas. Everything
    # above, including the two hardcoded TriggerBase rects, is expressed
    # against the pack render's origin. A zero margin leaves them alone.
    if margin:
        results = [(fn, t, ty, x + margin, y, w, h)
                   for (fn, t, ty, x, y, w, h) in results]

    if switch2:
        # The floating tiles live in the gutter, so they are authored in
        # the widened frame directly and take no shift.
        tile_x = (margin - SWPRO_TILE) // 2
        for target, x, label in (("LeftPaddle", tile_x, "GL"),
                                 ("RightPaddle", base_w - tile_x - SWPRO_TILE, "GR")):
            results.append(("NSwitchPro_GripTile.png", target, "Button",
                            x, SWPRO_TILE_Y, SWPRO_TILE, SWPRO_TILE))
            print(f"  {target:20s} ({label + ', floating tile':20s}) -> "
                  f"({x:4d}, {SWPRO_TILE_Y:4d}) {SWPRO_TILE:4d}x{SWPRO_TILE:3d}")

    return {"base_width": base_w, "base_height": base_h, "results": results}


def _add_trigger_base_entries(results):
    """For each Trigger element, emit a paired TriggerBase entry that
    points at the rest-state PNG (same filename minus '-Active' or
    '_Active') and shares position/size. View renders TriggerBase
    always-visible below Trigger, giving the trigger silhouette a
    rest-state visual under the active-press fill overlay."""
    out = []
    for entry in results:
        out.append(entry)
        fn, target, etype, x, y, w, h = entry
        if etype != "Trigger":
            continue
        # Strip Active suffix in either form
        rest_fn = fn.replace("_Active.png", ".png").replace("-Active.png", ".png")
        if rest_fn == fn:
            continue  # no -Active suffix → can't derive rest filename
        out.append((rest_fn, target + "Base", "TriggerBase", x, y, w, h))
        print(f"  TRIG-BASE  {target+'Base':20s}: {rest_fn} at ({x},{y}) {w}x{h}")
    return out


def _hit_polygons(overlay_path, _cache={}):
    """Trace the overlay's opaque region into simplified polygons for
    per-pixel hit-testing (the view clips the hover/click rectangle to
    this geometry, so a control only answers where its art shows).

    The mask is dilated a few native pixels first so thin strokes (the
    ZL/ZR arcs, D-pad arrow outlines) keep a comfortable grab margin
    while still hugging the visible art. Points are emitted normalized
    to the image so the view scales them to the rendered entry size.
    Returns "x,y x,y ...;x,y ..." (one group per polygon) or None.
    """
    if overlay_path in _cache:
        return _cache[overlay_path]
    ov = cv2.imread(overlay_path, cv2.IMREAD_UNCHANGED)
    if ov is None or ov.ndim < 3 or ov.shape[2] < 4:
        _cache[overlay_path] = None
        return None
    h, w = ov.shape[:2]
    mask = (ov[:, :, 3] > 25).astype(np.uint8)
    k = max(7, int(round(min(w, h) * 0.06)))
    if k % 2 == 0:
        k += 1
    mask = cv2.dilate(mask, np.ones((k, k), np.uint8))
    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    polys = []
    for c in contours:
        if cv2.contourArea(c) < 30:
            continue
        eps = 0.004 * cv2.arcLength(c, True)
        c = cv2.approxPolyDP(c, eps, True).reshape(-1, 2)
        if len(c) < 3:
            continue
        polys.append(" ".join("%.4f,%.4f" % (px / w, py / h) for px, py in c))
    result = ";".join(polys) if polys else None
    _cache[overlay_path] = result
    return result


def _prepare_steamdeck_base():
    """Build STEAMDECK/SD_base.png from the pack's Compact render.

    Three edits, all reproducible from the source art so this never
    becomes a hand-painted asset:

    1. The chroma-green screen is keyed to a dark panel. That is a
       literal green screen used for its authored purpose.

    2. The L1/L2/R1/R2 callout tiles are ERASED. The Compact view
       carries a labeled tile for every input, but those four controls
       are already drawn on the body as their own trigger and shoulder
       arcs, which is where the SVG labels them and where this layout
       anchors them. Keeping the tiles would show each of those inputs
       twice, with only the body copy ever lighting up.

    3. The remaining L4/L5/R4/R5 tiles are DESATURATED. They stay,
       because a rear paddle has no other representation on a front
       view, but the pack authors them in its own blue, which is off
       this app's palette. Neutral grey matches the body, and the ember
       comes from the tinted overlay drawn on top, exactly as it does
       for every other element.

    Tiles are found by their blue, not by hardcoded rectangles, and
    split top-pair vs side-pair by position.
    """
    src = os.path.join(ASSET_PACK, "Steam Deck Images", "Template",
                       "Steam Deck Compact.png")
    im = cv2.imread(src, cv2.IMREAD_UNCHANGED)
    b, g, r, a = [im[:, :, i].astype(int) for i in range(4)]

    green = (g > 120) & (g - r > 60) & (g - b > 60)
    im[green] = (28, 23, 20, 255)

    blue_raw = ((b > 70) & (b - r > 25) & (a > 60))
    blue = cv2.morphologyEx(blue_raw.astype(np.uint8) * 255,
                            cv2.MORPH_CLOSE, np.ones((15, 15), np.uint8))
    n, labels, stats, cent = cv2.connectedComponentsWithStats(blue, 8)
    erased = 0
    pair_masks = []
    for i in range(1, n):
        if stats[i][4] < 3000:
            continue
        region = labels == i
        if cent[i][1] < im.shape[0] / 3:      # top pair: L1/L2 and R1/R2
            im[region] = (0, 0, 0, 0)
            erased += 1
        else:                                  # side pair: L4/L5 and R4/R5
            pair_masks.append(region)
            px = im[region]
            lum = (px[:, 0] * 0.114 + px[:, 1] * 0.587 + px[:, 2] * 0.299)
            px[:, 0] = px[:, 1] = px[:, 2] = lum.astype(np.uint8)
            im[region] = px

    # Each side region is a STACKED PAIR of tiles. Split them apart on the
    # empty scanlines between, so every paddle gets its own rect.
    tiles = []
    for region in pair_masks:
        ys, xs = np.where(region)
        x0, x1 = xs.min(), xs.max()
        # Split on the UNCLOSED mask: the morphological close that makes
        # the pair one component also bridges the gap between its tiles.
        rows = np.where((region & blue_raw).any(axis=1))[0]
        runs, start = [], rows[0]
        for a_, b_ in zip(rows, rows[1:]):
            if b_ - a_ > 1:
                runs.append((start, a_)); start = b_
        runs.append((start, rows[-1]))
        for top, bot in runs:
            tiles.append((int(x0), int(top), int(x1 - x0 + 1), int(bot - top + 1)))
    tiles.sort(key=lambda t: (t[0] > im.shape[1] / 2, t[1]))

    # The paddle highlight is the TILE'S OWN silhouette, lifted from the
    # art rather than approximated. SD_BackButton.png cannot serve: it is
    # a 98%-opaque square, so on a rounded tile it fills a block and
    # leaves the border and the "L4" legend showing around it.
    # Drawn as a RING plus a wash, not a solid fill: the tile carries its
    # own "L4"/"L5" legend, and a solid highlight buries it, so with both
    # paddles bound you cannot tell which is which. The ring reads as
    # bound while the legend stays readable underneath, which is also how
    # the trackpad-click overlay in this same pack is authored.
    if tiles:
        x, y, w, h = tiles[0]
        solid = (im[y:y + h, x:x + w, 3] > 8).astype(np.uint8)
        inner = cv2.erode(solid, np.ones((3, 3), np.uint8), iterations=9)
        sil = np.zeros((h, w, 4), np.uint8)
        sil[:, :, :3] = 255
        sil[:, :, 3] = np.where(solid > 0, np.where(inner > 0, 96, 255), 0)
        cv2.imwrite(os.path.join(MODELS_DIR, "STEAMDECK", "SD_CompactTile.png"), sil)

    print(f"  base: keyed {int(green.sum())} green px, "
          f"erased {erased} shoulder/trigger tiles, "
          f"desaturated {len(pair_masks)} paddle tile pairs -> {len(tiles)} tiles")
    cv2.imwrite(os.path.join(MODELS_DIR, "STEAMDECK", "SD_base.png"), im)
    return tiles


def process_steamdeck():
    """Extract Steam Deck overlay positions.

    Same posture as Switch Pro: the pack's theme SVG labels sit ON the
    controls, so each label's bbox is the authoritative measurement and
    every press overlay is fit_overlay_to_bbox-sized to it. No composite
    refinement pass is needed.

    The Deck ships ONE face-button cap, ONE trackpad-click overlay and
    ONE rear-paddle overlay, reused at each lettered/side position
    exactly like the DS4 flow reuses DS4_Face_Button.png.

    THE ALTERNATIVE OVERLAY IS THE ONE TO USE, not the plain VSCView
    overlay, because it is the composition that places the rear
    paddles. In the plain overlay L4/L5/R4/R5 are labeled but parked
    off-canvas (L4/L5 at x=-149, R4/R5 at x=1879 on an 1860-wide
    canvas), so a layout built on it can only omit them. The
    Alternative widens the canvas and brings all four on-body. Its
    raster is 'Steam Deck Compact.png': the two agree on aspect to four
    decimals (593.72x247.22mm = 2.4016, 2241x933 = 2.4020), which is
    what identifies them as a pair, since neither filename says so.

    The shipped base is that Compact render with its chroma-green screen
    keyed to a dark panel; the green is a literal green screen, keyed
    for its authored purpose, not repainted art.

    Paddle target names follow the translator, not Valve's labels:
    PhysicalSlotResolver maps button_back_right -> Paddle1,
    button_back_left -> Paddle2, button_back_right_upper -> Paddle3,
    button_back_left_upper -> Paddle4. So R4=Paddle1, L4=Paddle2,
    R5=Paddle3, L5=Paddle4.
    """
    svg_path = os.path.join(ASSET_PACK, "Steam Deck Images",
        "Theme SVG", "Steam Deck Alternative Overlay.svg")

    paddle_tiles = _prepare_steamdeck_base()

    root = etree.parse(svg_path).getroot()
    base = cv2.imread(os.path.join(MODELS_DIR, "STEAMDECK", "SD_base.png"), cv2.IMREAD_UNCHANGED)
    base_w, base_h = base.shape[1], base.shape[0]

    vb = [float(v) for v in root.get("viewBox").split()]
    scale = base_w / vb[2]

    ov_dir = os.path.join(MODELS_DIR, "STEAMDECK")
    results = []

    def add(svg_label, filename, target, elem_type, fit_scale=1.0):
        bbox = get_element_pixel_bbox(root, svg_label, scale)
        if bbox is None:
            print(f"  MISS: {svg_label}")
            return None
        pos = fit_overlay_to_bbox(bbox, os.path.join(ov_dir, filename), scale=fit_scale)
        results.append((filename, target, elem_type, pos[0], pos[1], pos[2], pos[3]))
        print(f"  {target:20s} ({svg_label:20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")
        return bbox

    print("Parsing Steam Deck SVG elements...")

    add("L2 Trigger", "SD_L2.png", "LeftTrigger", "Trigger")
    add("R2 Trigger", "SD_R2.png", "RightTrigger", "Trigger")
    add("L1 Button", "SD_L1.png", "LeftShoulder", "Button")
    add("R1 Button", "SD_R1.png", "RightShoulder", "Button")

    # Face cluster: Xbox-style lettering, one shared cap overlay.
    add("A Button", "SD_Face_Button.png", "ButtonA", "Button")
    add("B Button", "SD_Face_Button.png", "ButtonB", "Button")
    add("X Button", "SD_Face_Button.png", "ButtonX", "Button")
    add("Y Button", "SD_Face_Button.png", "ButtonY", "Button")

    # D-pad: only the full cross is labeled, so the same edge-anchored
    # quadrant split the Xbox One / Series / Switch Pro flows use.
    dpad_bbox = get_element_pixel_bbox(root, "D-PAD", scale)
    if dpad_bbox:
        dx, dy, dw, dh = dpad_bbox
        half_w, half_h = dw / 2.0, dh / 2.0
        for direction, fn, target, sub in [
            ("Up",    "SD_D-PAD_Up.png",    "DPadUp",    (dx, dy, dw, half_h)),
            ("Down",  "SD_D-PAD_Down.png",  "DPadDown",  (dx, dy + dh - half_h, dw, half_h)),
            ("Left",  "SD_D-PAD_Left.png",  "DPadLeft",  (dx, dy, half_w, dh)),
            ("Right", "SD_D-PAD_Right.png", "DPadRight", (dx + dw - half_w, dy, half_w, dh)),
        ]:
            pos = fit_overlay_to_bbox(sub, os.path.join(ov_dir, fn))
            results.append((fn, target, "Button", pos[0], pos[1], pos[2], pos[3]))
            print(f"  {target:20s} ({'D-PAD ' + direction:20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")

    add("View", "SD_View-Menu_Button.png", "ButtonBack", "Button")
    add("Menu", "SD_View-Menu_Button.png", "ButtonStart", "Button")
    add("Guide Button", "SD_Guide-QuickMenu_Button.png", "ButtonGuide", "Button")
    add("Quick Access", "SD_Guide-QuickMenu_Button.png", "ButtonQuickAccess", "Button")

    add("Left Joystick", "SD_LeftAnalogStick.png", "LeftThumbRing", "StickRing")
    add("Right Joystick", "SD_RightAnalogStick.png", "RightThumbRing", "StickRing")
    for lbl, target in [("Left Joystick", "LeftThumbButton"),
                        ("Right Joystick", "RightThumbButton")]:
        bbox = get_element_pixel_bbox(root, lbl, scale)
        if bbox:
            pos = fit_overlay_to_bbox(bbox, os.path.join(ov_dir, "SD_Joystick_Click.png"))
            results.append(("SD_Joystick_Click.png", target, "StickClick", pos[0], pos[1], pos[2], pos[3]))
            print(f"  {target:20s} ({lbl:20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")

    # Trackpads: click highlight plus the finger-dot zone, the DualSense
    # Touchpad/TouchpadClick pairing widened to left and right.
    for lbl, side in [("Left Pad", "Left"), ("Right Pad", "Right")]:
        bbox = get_element_pixel_bbox(root, lbl, scale)
        if bbox is None:
            print(f"  MISS: {lbl}")
            continue
        pos = fit_overlay_to_bbox(bbox, os.path.join(ov_dir, "SD_Touchpad_Click.png"))
        results.append(("SD_Touchpad_Click.png", side + "TouchpadClick", "Button", pos[0], pos[1], pos[2], pos[3]))
        results.append(("", side + "Touchpad", "Touchpad", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {side + 'TouchpadClick':20s} ({lbl:20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")

    # Rear paddles, on-body only in this overlay (see the docstring).
    # Positioned on the TILE rects measured from the art, not on the SVG
    # label bbox: the label marks the legend glyph inside the tile, while
    # the tile is the whole control the highlight has to cover.
    # _prepare_steamdeck_base returns them ordered left-top, left-bottom,
    # right-top, right-bottom -> L4, L5, R4, R5.
    for (x, y, w, h), lbl, target in zip(
            paddle_tiles, ["L4", "L5", "R4", "R5"],
            ["Paddle2", "Paddle4", "Paddle1", "Paddle3"]):
        results.append(("SD_CompactTile.png", target, "Button", x, y, w, h))
        print(f"  {target:20s} ({lbl + ' tile':20s}) -> ({x:4d}, {y:4d}) {w:4d}x{h:3d}")

    return {"base_width": base_w, "base_height": base_h, "results": results}


def process_steamcontroller():
    """Extract Steam Controller (2026 retail name: the original Valve
    pad) overlay positions from the pack's Black theme SVG.

    Two departures from every other layout, both physical rather than
    incidental: the pad has no D-pad (the LEFT trackpad serves that
    role, and the SVG labels the cross as trackpad outline art), and it
    carries a single analog stick, so no Right* stick entries exist.

    The rear grip paddles are the one element the SVG does not label.
    Their positions come from differencing the pack's own
    'Active Button View' template against the plain overlay -- the same
    template-difference technique the Switch Pro rest-state triggers
    use -- which isolates each grip to a 184x327 region matching
    SC_LeftGrip_Button.png's native 183x327 to within a pixel.
    """
    svg_path = os.path.join(ASSET_PACK, "Steam Controller Images",
        "Default Theme", "Theme SVG", "Black", "Steam Controller VSCView.svg")

    root = etree.parse(svg_path).getroot()
    base = cv2.imread(os.path.join(MODELS_DIR, "STEAMCONTROLLER", "SC_base.png"), cv2.IMREAD_UNCHANGED)
    base_w, base_h = base.shape[1], base.shape[0]

    vb = [float(v) for v in root.get("viewBox").split()]
    scale = base_w / vb[2]

    ov_dir = os.path.join(MODELS_DIR, "STEAMCONTROLLER")
    results = []

    def add(svg_label, filename, target, elem_type, fit_scale=1.0):
        bbox = get_element_pixel_bbox(root, svg_label, scale)
        if bbox is None:
            print(f"  MISS: {svg_label}")
            return None
        pos = fit_overlay_to_bbox(bbox, os.path.join(ov_dir, filename), scale=fit_scale)
        results.append((filename, target, elem_type, pos[0], pos[1], pos[2], pos[3]))
        print(f"  {target:20s} ({svg_label:20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")
        return bbox

    print("Parsing Steam Controller SVG elements...")

    add("Left Trigger", "SC_LeftTrigger-FullPull-Active.png", "LeftTrigger", "Trigger")
    add("Right Trigger", "SC_RightTrigger-FullPull-Active.png", "RightTrigger", "Trigger")
    add("Left Bumper", "SC_LeftBumper-Active.png", "LeftShoulder", "Button")
    add("Right Bumper", "SC_RightBumper-Active.png", "RightShoulder", "Button")

    add("A", "SC_Face_Button.png", "ButtonA", "Button")
    add("B", "SC_Face_Button.png", "ButtonB", "Button")
    add("X", "SC_Face_Button.png", "ButtonX", "Button")
    add("Y", "SC_Face_Button.png", "ButtonY", "Button")

    add("Select Button", "SC_Start-Select_Button.png", "ButtonBack", "Button")
    add("Start Button", "SC_Start-Select_Button.png", "ButtonStart", "Button")
    add("Steam Guide Button", "SC_Guide_Button.png", "ButtonGuide", "Button")

    add("Analog Stick", "SC_AnalogStick.png", "LeftThumbRing", "StickRing")
    bbox = get_element_pixel_bbox(root, "Analog Stick", scale)
    if bbox:
        pos = fit_overlay_to_bbox(bbox, os.path.join(ov_dir, "SC_AnalogStick_Click.png"))
        results.append(("SC_AnalogStick_Click.png", "LeftThumbButton", "StickClick", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {'LeftThumbButton':20s} ({'Analog Stick':20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")

    for lbl, side, fn in [("Left Touchpad Color", "Left", "SC_LeftTrackpad_Click.png"),
                          ("Right Touchpad Color", "Right", "SC_RightTrackpad_Click.png")]:
        bbox = get_element_pixel_bbox(root, lbl, scale)
        if bbox is None:
            print(f"  MISS: {lbl}")
            continue
        pos = fit_overlay_to_bbox(bbox, os.path.join(ov_dir, fn))
        results.append((fn, side + "TouchpadClick", "Button", pos[0], pos[1], pos[2], pos[3]))
        results.append(("", side + "Touchpad", "Touchpad", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {side + 'TouchpadClick':20s} ({lbl:20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")

    # Grips: unlabeled in the SVG, measured by template difference (see
    # the docstring). Position is exact at base-canvas registration, so
    # no fitting applies.
    results.append(("SC_LeftGrip_Button.png", "LeftGrip", "Button", 215, 560, 183, 327))
    print(f"  {'LeftGrip':20s} ({'template diff':20s}) -> ( 215,  560)  183x327")
    results.append(("SC_RightGrip_Button.png", "RightGrip", "Button", 1068, 563, 183, 327))
    print(f"  {'RightGrip':20s} ({'template diff':20s}) -> (1068,  563)  183x327")

    return {"base_width": base_w, "base_height": base_h, "results": results}


def generate_csharp(layouts, output_path):
    """Generate C# source file with overlay position data."""
    lines = [
        "// AUTO-GENERATED by tools/overlay_positions.py -- do not edit manually",
        "namespace PadForge.Models2D;",
        "",
        "public enum OverlayElementType { Button, Trigger, TriggerBase, StickRing, StickClick, FaceButtonGroup, Touchpad }",
        "",
        "public record OverlayElement(string ImageFile, string TargetName, OverlayElementType ElementType, double X, double Y, double Width, double Height, string HitPath = null);",
        "",
    ]

    def emit(class_name, data, base_path, stick_travel):
        folder = base_path.split("/")[1]
        ov_dir = os.path.join(MODELS_DIR, folder)
        lines.append(f"public static class {class_name}")
        lines.append("{")
        lines.append(f"    public const int BaseWidth = {data['base_width']};")
        lines.append(f"    public const int BaseHeight = {data['base_height']};")
        lines.append(f'    public const string BasePath = "{base_path}";')
        lines.append(f"    public const double StickMaxTravel = {stick_travel};")
        lines.append("")
        lines.append("    public static readonly OverlayElement[] Overlays =")
        lines.append("    {")
        for fn, target, etype, x, y, w, h in data["results"]:
            hit = _hit_polygons(os.path.join(ov_dir, fn)) if fn and etype not in ("TriggerBase",) else None
            if hit:
                lines.append(f'        new("{fn}", "{target}", OverlayElementType.{etype}, {x}, {y}, {w}, {h}, "{hit}"),')
            else:
                lines.append(f'        new("{fn}", "{target}", OverlayElementType.{etype}, {x}, {y}, {w}, {h}),')
        lines.append("    };")
        lines.append("}")

    for i, (class_name, data, base_path, stick_travel) in enumerate(layouts):
        if i > 0:
            lines.append("")
        emit(class_name, data, base_path, stick_travel)

    with open(output_path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print(f"\nGenerated: {output_path}")


def main():
    print("=== Xbox 360 Controller ===")
    xbox_data = process_xbox360()
    print(f"\n  Total Xbox 360 overlays: {len(xbox_data['results'])}")

    print("\n=== DualShock 4 Controller ===")
    ds4_data = process_ds4()
    print(f"\n  Total DS4 overlays: {len(ds4_data['results'])}")

    print("\n=== DualSense Controller ===")
    dualsense_data = process_dualsense()
    print(f"\n  Total DualSense overlays: {len(dualsense_data['results'])}")

    print("\n=== Xbox One S Controller ===")
    xbone_data = process_xbox_one_s()
    print(f"\n  Total Xbox One S overlays: {len(xbone_data['results'])}")

    print("\n=== Xbox Series X Controller ===")
    xbseries_data = process_xbox_series()
    print(f"\n  Total Xbox Series X overlays: {len(xbseries_data['results'])}")

    print("\n=== Switch Pro Controller ===")
    swpro_data = process_switchpro()
    print(f"\n  Total Switch Pro overlays: {len(swpro_data['results'])}")

    print("\n=== Switch 2 Pro Controller ===")
    swpro2_data = process_switch2pro()
    print(f"\n  Total Switch 2 Pro overlays: {len(swpro2_data['results'])}")

    print("\n=== Steam Deck ===")
    deck_data = process_steamdeck()
    print(f"\n  Total Steam Deck overlays: {len(deck_data['results'])}")

    print("\n=== Steam Controller ===")
    steamc_data = process_steamcontroller()
    print(f"\n  Total Steam Controller overlays: {len(steamc_data['results'])}")

    # Inject TriggerBase entries (rest-state trigger image under each
    # active-press blue overlay). Done after all profile-specific
    # processing so the rest-state inherits the final trigger
    # position/size.
    # Steam Deck and Steam Controller are deliberately excluded: their
    # shipped base renders already draw the triggers at rest (unlike the
    # Switch Pro base, which is the pack's trigger-LESS variant), and the
    # Steam packs ship no rest-state trigger PNG for the pass to point at.
    for data in [xbox_data, ds4_data, dualsense_data, xbone_data, xbseries_data, swpro_data,
                 swpro2_data]:
        data["results"] = _add_trigger_base_entries(data["results"])

    # Hit-test precedence: the view's hover/click rectangles resolve to
    # the LAST-added overlay in an overlap, and every layout's trigger
    # bbox runs 10-70 px down behind its bumper (the lower part of the
    # trigger art is hidden by the body). With triggers emitted after
    # bumpers, a cursor well below the visible trigger arc highlighted
    # the trigger instead of the bumper (reported on Switch Pro,
    # measured on all six layouts). Stable-move Trigger + TriggerBase
    # entries to the front so bumpers win the shared band; visual
    # stacking is unaffected (Z-indices are explicit in the view).
    for data in [xbox_data, ds4_data, dualsense_data, xbone_data, xbseries_data, swpro_data, swpro2_data,
                 deck_data, steamc_data]:
        rs = data["results"]
        trig = [r for r in rs if r[2] in ("Trigger", "TriggerBase")]
        rest = [r for r in rs if r[2] not in ("Trigger", "TriggerBase")]
        data["results"] = trig + rest

    # Sanity checks
    for name, data in [("Xbox 360", xbox_data), ("DS4", ds4_data),
                       ("DualSense", dualsense_data),
                       ("Xbox One S", xbone_data),
                       ("Xbox Series X", xbseries_data),
                       ("Switch Pro", swpro_data),
                       ("Switch 2 Pro", swpro2_data),
                       ("Steam Deck", deck_data),
                       ("Steam Controller", steamc_data)]:
        bw, bh = data["base_width"], data["base_height"]
        for fn, target, _, x, y, w, h in data["results"]:
            if x < -10 or y < -10 or x + w > bw + 10 or y + h > bh + 10:
                print(f"  WARNING [{name}]: {target} at ({x},{y}) {w}x{h} out of bounds (base {bw}x{bh})")

    output_dir = os.path.join(PROJ_ROOT, "PadForge.App", "Models2D")
    os.makedirs(output_dir, exist_ok=True)
    layouts = [
        ("Xbox360Layout",       xbox_data,      "2DModels/XBOX360/XB360_base.png",         30),
        ("DS4Layout",           ds4_data,       "2DModels/DS4/DS4_V2_base.png",            25),
        ("DualSenseLayout",     dualsense_data, "2DModels/DualSense/DualSense_base.png",   25),
        ("XboxOneSLayout",      xbone_data,     "2DModels/XBOXONE/XB1_S_base.png",         30),
        ("XboxSeriesXLayout",   xbseries_data,  "2DModels/XBOXSERIES/XBSeries_base.png",   30),
        ("SwitchProLayout",     swpro_data,     "2DModels/SWITCHPRO/NSwitchPro_base.png",  25),
        ("Switch2ProLayout",    swpro2_data,    "2DModels/SWITCH2PRO/NSwitchPro_base.png", 25),
        ("SteamDeckLayout",      deck_data,      "2DModels/STEAMDECK/SD_base.png",          22),
        ("SteamControllerLayout", steamc_data,   "2DModels/STEAMCONTROLLER/SC_base.png",    28),
    ]
    generate_csharp(layouts, os.path.join(output_dir, "ControllerOverlayLayout.cs"))
    print("\nDone!")


if __name__ == "__main__":
    main()
