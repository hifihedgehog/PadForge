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


def refine_with_composite(composite_path, results, search_radius=40):
    """Refine overlay positions using alpha-channel template matching against full composite.

    The composite overlay image has all highlights pre-positioned correctly.
    For each overlay, we search in a neighborhood around the SVG-derived position
    and use the best alpha-channel match as the refined position.
    """
    composite = cv2.imread(composite_path, cv2.IMREAD_UNCHANGED)
    if composite is None or composite.shape[2] < 4:
        print("  WARNING: Could not load composite overlay for refinement")
        return results

    comp_alpha = composite[:, :, 3].astype(np.float32)
    comp_h, comp_w = comp_alpha.shape

    refined = []
    for filename, target, etype, x, y, w, h in results:
        overlay_path = os.path.join(os.path.dirname(composite_path), filename)
        ov = cv2.imread(overlay_path, cv2.IMREAD_UNCHANGED)
        if ov is None or ov.shape[2] < 4:
            refined.append((filename, target, etype, x, y, w, h))
            continue

        ov_alpha = ov[:, :, 3].astype(np.float32)
        ov_h, ov_w = ov_alpha.shape

        # Define search region around SVG position
        sx = max(0, x - search_radius)
        sy = max(0, y - search_radius)
        ex = min(comp_w, x + ov_w + search_radius)
        ey = min(comp_h, y + ov_h + search_radius)

        # Ensure search region can fit the template
        if ex - sx < ov_w or ey - sy < ov_h:
            refined.append((filename, target, etype, x, y, w, h))
            continue

        search_region = comp_alpha[sy:ey, sx:ex]

        try:
            result = cv2.matchTemplate(search_region, ov_alpha, cv2.TM_CCOEFF_NORMED)
            _, max_val, _, max_loc = cv2.minMaxLoc(result)

            if max_val > 0.3:
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

    return {"base_width": base.shape[1], "base_height": base.shape[0], "results": results}


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

    return {"base_width": base.shape[1], "base_height": base.shape[0], "results": results}


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

    def add(svg_label, filename, target, elem_type):
        bbox = get_element_pixel_bbox(root, svg_label, scale)
        if bbox is None:
            print(f"  MISS: {svg_label}")
            return None
        overlay_path = os.path.join(ov_dir, filename)
        pos = center_overlay_on_bbox(bbox, overlay_path)
        results.append((filename, target, elem_type, pos[0], pos[1], pos[2], pos[3]))
        print(f"  {target:20s} ({svg_label:20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")
        return bbox

    print("Parsing DualSense SVG elements...")

    # Face buttons — separate PNG per button (Cross/Circle/Square/Triangle).
    # Note SVG label "Crosss" has an extra 's' (asset-pack typo).
    add("Crosss", "DualSense_Cross.png", "ButtonA", "Button")
    add("Circle", "DualSense_Circle.png", "ButtonB", "Button")
    add("Square", "DualSense_Square.png", "ButtonX", "Button")
    add("Triangle ", "DualSense_Triangle.png", "ButtonY", "Button")  # trailing space in label

    # D-Pad
    add("D-PAD Up", "DualSense_D-PAD_Up.png", "DPadUp", "Button")
    add("D-PAD Down", "DualSense_D-PAD_Down.png", "DPadDown", "Button")
    add("D-PAD Left", "DualSense_D-PAD_Left.png", "DPadLeft", "Button")
    add("D-PAD Right", "DualSense_D-PAD_Right.png", "DPadRight", "Button")

    # Bumpers
    add("L1", "DualSense_L1-Active.png", "LeftShoulder", "Button")
    add("R1", "DualSense_R1-Active.png", "RightShoulder", "Button")

    # Triggers (note SVG plurality typos: "L2 Triggers", "R2 Trigger")
    add("L2 Triggers", "DualSense_L2-Active.png", "LeftTrigger", "Trigger")
    add("R2 Trigger", "DualSense_R2-Active.png", "RightTrigger", "Trigger")

    # Create / Option / PS buttons
    add("Create Button", "DualSense_Create_Button.png", "ButtonBack", "Button")
    add("Option Button", "DualSense_Option_Button.png", "ButtonStart", "Button")
    add("PS Button", "DualSense_Home_Button.png", "ButtonGuide", "Button")

    # Sticks (rings) and stick clicks share the same SVG bbox
    add("Left Stick", "DualSense_LeftAnalogStick.png", "LeftThumbRing", "StickRing")
    add("Right Stick", "DualSense_RightAnalogStick.png", "RightThumbRing", "StickRing")
    left_bbox = get_element_pixel_bbox(root, "Left Stick", scale)
    right_bbox = get_element_pixel_bbox(root, "Right Stick", scale)
    if left_bbox:
        pos = center_overlay_on_bbox(left_bbox, os.path.join(ov_dir, "DualSense_AnalogStick_Click.png"))
        results.append(("DualSense_AnalogStick_Click.png", "LeftThumbButton", "StickClick", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {'LeftThumbButton':20s} ({'Left Stick':20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")
    if right_bbox:
        pos = center_overlay_on_bbox(right_bbox, os.path.join(ov_dir, "DualSense_AnalogStick_Click.png"))
        results.append(("DualSense_AnalogStick_Click.png", "RightThumbButton", "StickClick", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {'RightThumbButton':20s} ({'Right Stick':20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")

    # Refine via composite alpha-channel template matching.
    composite_path = os.path.join(ov_dir, "DualSense Controller Overlay.png")
    print("\nRefining DualSense positions via alpha-channel template matching...")
    results = refine_with_composite(composite_path, results)

    # Touchpad zones — no SVG label, use rectangle estimates derived from the
    # DualSense layout (touchpad surface sits between the Create + Option
    # buttons, with a click strip running along its top edge below the
    # lightbar). Same shape as DS4Layout. Tweak in-app if alignment drifts.
    tp_w = round(base_w * 0.34)         # ~0.34 of base width
    tp_h = round(base_h * 0.27)         # ~0.27 of base height
    tp_x = round((base_w - tp_w) / 2)   # horizontally centered
    tp_y = round(base_h * 0.27)         # below the lightbar
    click_h = round(tp_h * 0.16)
    click_y = max(0, tp_y - click_h - 4)
    results.append(("", "TouchpadClick", "Button", tp_x, click_y, tp_w, click_h))
    results.append(("", "Touchpad", "Touchpad", tp_x, tp_y, tp_w, tp_h))
    print(f"  {'TouchpadClick':20s} (manual zone)         -> ({tp_x}, {click_y}) {tp_w}x{click_h}")
    print(f"  {'Touchpad':20s} (manual zone)         -> ({tp_x}, {tp_y}) {tp_w}x{tp_h}")

    return {"base_width": base_w, "base_height": base_h, "results": results}


def _process_xbox_modern(profile_name, svg_path, base_relpath, ov_subdir,
                        composite_filename, prefix,
                        face_btn_filenames, dpad_filenames,
                        bumper_filenames, trigger_filenames,
                        stick_filenames, stick_click_filename,
                        guide_filename, menu_filename, view_filename,
                        share_filename=None):
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

    def add(svg_label, filename, target, elem_type):
        bbox = get_element_pixel_bbox(root, svg_label, scale)
        if bbox is None:
            print(f"  MISS: {svg_label}")
            return None
        overlay_path = os.path.join(ov_dir, filename)
        pos = center_overlay_on_bbox(bbox, overlay_path)
        results.append((filename, target, elem_type, pos[0], pos[1], pos[2], pos[3]))
        print(f"  {target:20s} ({svg_label:20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")
        return bbox

    print(f"Parsing {profile_name} SVG elements...")

    # Face buttons (individual labels in both SVGs).
    add("A Button", face_btn_filenames["A"], "ButtonA", "Button")
    add("B Button", face_btn_filenames["B"], "ButtonB", "Button")
    add("X Button", face_btn_filenames["X"], "ButtonX", "Button")
    add("Y Button", face_btn_filenames["Y"], "ButtonY", "Button")

    # Bumpers — split the group bbox into left/right halves since neither SVG
    # tags individual L/R bumpers. The press overlay PNG carries the right
    # shape; we just need to land it in roughly the correct half.
    bumper_label = bumper_filenames["GroupLabel"]
    bumper_bbox = get_element_pixel_bbox(root, bumper_label, scale)
    if bumper_bbox:
        bx, by, bw, bh = bumper_bbox
        for side, fn, target in [("L", bumper_filenames["L"], "LeftShoulder"),
                                 ("R", bumper_filenames["R"], "RightShoulder")]:
            ov = cv2.imread(os.path.join(ov_dir, fn), cv2.IMREAD_UNCHANGED)
            half_x = bx if side == "L" else bx + bw / 2
            cx = half_x + bw / 4
            cy = by + bh / 2
            x = round(cx - ov.shape[1] / 2)
            y = round(cy - ov.shape[0] / 2)
            results.append((fn, target, "Button", x, y, ov.shape[1], ov.shape[0]))
            print(f"  {target:20s} ({bumper_label} half {side}) -> ({x:4d}, {y:4d}) {ov.shape[1]:4d}x{ov.shape[0]:3d}")

    # Triggers
    add(trigger_filenames["LLabel"], trigger_filenames["L"], "LeftTrigger", "Trigger")
    add(trigger_filenames["RLabel"], trigger_filenames["R"], "RightTrigger", "Trigger")

    # System buttons — Xbox Series adds Share; both have Menu / View / Guide.
    add("Menu Button", menu_filename, "ButtonStart", "Button")
    add("View Button", view_filename, "ButtonBack", "Button")
    # Guide button — group has hub LEDs; prefer the "Xbox Button" inner label
    # if present, fall back to the full guide group label.
    guide_bbox = get_element_pixel_bbox(root, "Xbox Button", scale)
    if guide_bbox is None:
        guide_bbox = get_element_pixel_bbox(root, "Xbox Guide Button", scale)
    if guide_bbox:
        pos = center_overlay_on_bbox(guide_bbox, os.path.join(ov_dir, guide_filename))
        results.append((guide_filename, "ButtonGuide", "Button", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {'ButtonGuide':20s} ({'Xbox Button/Guide':20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")

    # Sticks
    add("Left Stick", stick_filenames["L"], "LeftThumbRing", "StickRing")
    add("Right Stick", stick_filenames["R"], "RightThumbRing", "StickRing")
    left_bbox = get_element_pixel_bbox(root, "Left Stick", scale)
    right_bbox = get_element_pixel_bbox(root, "Right Stick", scale)
    if left_bbox:
        pos = center_overlay_on_bbox(left_bbox, os.path.join(ov_dir, stick_click_filename))
        results.append((stick_click_filename, "LeftThumbButton", "StickClick", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {'LeftThumbButton':20s} ({'Left Stick':20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")
    if right_bbox:
        pos = center_overlay_on_bbox(right_bbox, os.path.join(ov_dir, stick_click_filename))
        results.append((stick_click_filename, "RightThumbButton", "StickClick", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {'RightThumbButton':20s} ({'Right Stick':20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")

    # D-PAD — pick whichever group label exists in this SVG and split into
    # quadrants. Same approach Xbox 360 uses.
    dpad_bbox = None
    for label in dpad_filenames["GroupLabels"]:
        dpad_bbox = get_element_pixel_bbox(root, label, scale)
        if dpad_bbox:
            print(f"  D-PAD using group label: {label}")
            break
    if dpad_bbox:
        dx, dy, dw, dh = dpad_bbox
        cx, cy = dx + dw / 2, dy + dh / 2
        for direction, fn, target in [("Up", dpad_filenames["Up"], "DPadUp"),
                                       ("Down", dpad_filenames["Down"], "DPadDown"),
                                       ("Left", dpad_filenames["Left"], "DPadLeft"),
                                       ("Right", dpad_filenames["Right"], "DPadRight")]:
            ov = cv2.imread(os.path.join(ov_dir, fn), cv2.IMREAD_UNCHANGED)
            ov_w, ov_h = ov.shape[1], ov.shape[0]
            if direction == "Up":
                x = round(cx - ov_w / 2); y = round(dy - ov_h * 0.1)
            elif direction == "Down":
                x = round(cx - ov_w / 2); y = round(dy + dh - ov_h * 0.9)
            elif direction == "Left":
                x = round(dx - ov_w * 0.1); y = round(cy - ov_h / 2)
            else:  # Right
                x = round(dx + dw - ov_w * 0.9); y = round(cy - ov_h / 2)
            results.append((fn, target, "Button", x, y, ov_w, ov_h))
            print(f"  {target:20s} ({'D-PAD computed':20s}) -> ({x:4d}, {y:4d}) {ov_w:4d}x{ov_h:3d}")

    # Refine via composite alpha-channel template matching.
    composite_path = os.path.join(ov_dir, composite_filename)
    print(f"\nRefining {profile_name} positions via alpha-channel template matching...")
    results = refine_with_composite(composite_path, results)

    return {"base_width": base_w, "base_height": base_h, "results": results}


def process_xbox_one_s():
    """Extract Xbox One S overlay positions."""
    svg_path = os.path.join(ASSET_PACK,
        "Xbox Wireless Controller Images", "Default Theme", "Theme SVG",
        "Xbox One Color", "Xbox One Controller VSCView White.svg")
    return _process_xbox_modern(
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
        stick_click_filename="XB1_LeftStick_Click.png",
        guide_filename="XB1_HomeButton.png",
        menu_filename="XB1_MenuButton.png",
        view_filename="XB1_ViewButton.png")


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
        stick_click_filename="XBSeries_LeftStick_Click.png",
        guide_filename="XBSeries_HomeButton.png",
        menu_filename="XBSeries_MenuButton.png",
        view_filename="XBSeries_ViewButton.png")
    # Xbox Series has a dedicated Share button between Menu and View.
    root = etree.parse(os.path.join(ASSET_PACK,
        "Xbox Wireless Controller Images", "Default Theme", "Theme SVG",
        "Xbox Series X Color", "Xbox Series X Controller VSCView White.svg")).getroot()
    share_bbox = get_element_pixel_bbox(root, "Share Button", 1.0)
    if share_bbox:
        ov_dir = os.path.join(MODELS_DIR, "XBOXSERIES")
        pos = center_overlay_on_bbox(share_bbox, os.path.join(ov_dir, "XBSeries_ShareButton.png"))
        data["results"].append(("XBSeries_ShareButton.png", "ButtonShare", "Button", pos[0], pos[1], pos[2], pos[3]))
        print(f"  {'ButtonShare':20s} ({'Share Button':20s}) -> ({pos[0]:4d}, {pos[1]:4d}) {pos[2]:4d}x{pos[3]:3d}")
    return data


def generate_csharp(layouts, output_path):
    """Generate C# source file with overlay position data."""
    lines = [
        "// AUTO-GENERATED by tools/overlay_positions.py -- do not edit manually",
        "namespace PadForge.Models2D;",
        "",
        "public enum OverlayElementType { Button, Trigger, StickRing, StickClick, FaceButtonGroup, Touchpad }",
        "",
        "public record OverlayElement(string ImageFile, string TargetName, OverlayElementType ElementType, double X, double Y, double Width, double Height);",
        "",
    ]

    def emit(class_name, data, base_path, stick_travel):
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

    # Sanity checks
    for name, data in [("Xbox 360", xbox_data), ("DS4", ds4_data),
                       ("DualSense", dualsense_data),
                       ("Xbox One S", xbone_data),
                       ("Xbox Series X", xbseries_data)]:
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
    ]
    generate_csharp(layouts, os.path.join(output_dir, "ControllerOverlayLayout.cs"))
    print("\nDone!")


if __name__ == "__main__":
    main()
