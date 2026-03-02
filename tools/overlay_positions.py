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

    # Face buttons — combined overlay (shows when any face button pressed; click quadrants map to individual buttons)
    add("Face Buttons", "DS4_Face_Button.png", "FaceButtonGroup", "FaceButtonGroup")

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


def generate_csharp(xbox_data, ds4_data, output_path):
    """Generate C# source file with overlay position data."""
    lines = [
        "// AUTO-GENERATED by tools/overlay_positions.py -- do not edit manually",
        "namespace PadForge.Models2D;",
        "",
        "public enum OverlayElementType { Button, Trigger, StickRing, StickClick, FaceButtonGroup }",
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

    emit("Xbox360Layout", xbox_data, "2DModels/XBOX360/XB360_base.png", 30)
    lines.append("")
    emit("DS4Layout", ds4_data, "2DModels/DS4/DS4_V2_base.png", 25)

    with open(output_path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print(f"\nGenerated: {output_path}")


def main():
    print("=== Xbox 360 Controller ===")
    xbox_data = process_xbox360()
    print(f"\n  Total Xbox overlays: {len(xbox_data['results'])}")

    print("\n=== DualShock 4 Controller ===")
    ds4_data = process_ds4()
    print(f"\n  Total DS4 overlays: {len(ds4_data['results'])}")

    # Sanity checks
    for name, data in [("Xbox 360", xbox_data), ("DS4", ds4_data)]:
        bw, bh = data["base_width"], data["base_height"]
        for fn, target, _, x, y, w, h in data["results"]:
            if x < -10 or y < -10 or x + w > bw + 10 or y + h > bh + 10:
                print(f"  WARNING [{name}]: {target} at ({x},{y}) {w}x{h} out of bounds (base {bw}x{bh})")

    output_dir = os.path.join(PROJ_ROOT, "PadForge.App", "Models2D")
    os.makedirs(output_dir, exist_ok=True)
    generate_csharp(xbox_data, ds4_data, os.path.join(output_dir, "ControllerOverlayLayout.cs"))
    print("\nDone!")


if __name__ == "__main__":
    main()
