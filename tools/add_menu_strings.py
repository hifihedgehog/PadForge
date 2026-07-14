"""Adds the radial / touch menu (#9 B-17, Wave 4c) string cluster.

English resx + hand-maintained Designer entries only (the localization
pass fills the sibling locales later; ResourceManager falls back to the
base resx until then). Idempotent: existing keys are skipped. BOM and
encoding preserved (feedback_powershell_utf8_roundtrip).
"""
import io
import os

ROOT = os.path.join(os.path.dirname(__file__), "..", "PadForge.App", "Resources", "Strings")

KEYS = {
    # Pad page tab + editor
    "Pad_Menus": "Menus",
    "Menu_EmptyHint": "Menus turn a stick or touchpad into an on-screen ring or grid. Add a menu, pick its host input, then label each cell and give it a binding.",
    "Menu_NewNameFormat": "Menu {0}",
    "Menu_Name": "Menu Name",
    "Menu_Enabled": "Enabled",
    "Menu_Style": "Style",
    "Menu_Style_Radial": "Radial Ring",
    "Menu_Style_Grid": "Touch Grid",
    "Menu_HostInput": "Host Input",
    "Menu_Host_LeftStick": "Left Stick",
    "Menu_Host_RightStick": "Right Stick",
    "Menu_Host_Touchpad_Format": "Touchpad {0}",
    "Menu_HostHalf": "Pad Half",
    "Menu_Half_Whole": "Whole Pad",
    "Menu_Half_Left": "Left Half",
    "Menu_Half_Right": "Right Half",
    "Menu_FireMode": "Fire Mode",
    "Menu_Fire_Click": "On Click",
    "Menu_Fire_ClickRelease": "On Click Release",
    "Menu_Fire_TouchRelease": "On Touch Release",
    "Menu_Fire_Always": "While Hovered",
    "Menu_Cells": "Cells",
    "Menu_HasCenter": "Center Cell",
    "Menu_ShowLabels": "Show Labels",
    "Menu_Position": "Screen Position",
    "Menu_Scale": "Size",
    "Menu_Opacity": "Opacity",
    "Menu_Deadzone": "Engage Deadzone",
    "Menu_CellBindings": "Cell Bindings",
    "Menu_CellIndex_Format": "Cell {0}",
    "Menu_CellCenter": "Center",
    "Menu_Binding_None": "None",
    "Menu_Binding_Key": "Keyboard Key",
    "Menu_Binding_Button": "Controller Button",
    # Mapping chip for imported rows / macro triggers
    "Mapping_MenuItem_Format": "Menu {0} Cell {1}",
    # Dashboard overlay toggle card
    "Dashboard_MenuOverlay": "Menu Overlay",
    "Dashboard_MenuOverlayDescription": "Show the on-screen ring or grid while a radial or touch menu is engaged. Menus still fire with the overlay off.",
    # Workshop translation reasons (translator v7)
    "Workshop_Tr_MenuEmitted": "on-screen menu created ({0} bound cells)",
    "Workshop_Tr_MenuEmpty": "menu has no bound cells",
    "Workshop_Tr_MenuSurfaceNotSupported": "menu host surface not supported ({0})",
    "Workshop_Tr_MenuIconsDropped": "menu cell icons dropped ({0} cells render text labels)",
    "Workshop_Tr_MenuTuningDropped": "menu tuning not supported ({0})",
}


def add_to_resx(path):
    with io.open(path, "r", encoding="utf-8-sig") as f:
        text = f.read()
    added = 0
    lines = []
    for key, value in KEYS.items():
        if f'<data name="{key}"' in text:
            continue
        esc = (value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;"))
        lines.append(f'  <data name="{key}" xml:space="preserve"><value>{esc}</value></data>')
        added += 1
    if added:
        text = text.replace("</root>", "\n".join(lines) + "\n</root>")
        with io.open(path, "w", encoding="utf-8-sig", newline="") as f:
            f.write(text)
    print(f"{os.path.basename(path)}: +{added}")


def add_to_designer(path):
    with io.open(path, "r", encoding="utf-8-sig") as f:
        text = f.read()
    added = 0
    lines = []
    for key in KEYS:
        if f'Get("{key}")' in text:
            continue
        lines.append(f'    public string {key} => Get("{key}");')
        added += 1
    if added:
        idx = text.rstrip().rfind("}")
        text = text[:idx] + "\n".join(lines) + "\n" + text[idx:]
        with io.open(path, "w", encoding="utf-8-sig", newline="") as f:
            f.write(text)
    print(f"{os.path.basename(path)}: +{added}")


add_to_resx(os.path.join(ROOT, "Strings.resx"))
add_to_designer(os.path.join(ROOT, "Strings.Designer.cs"))
