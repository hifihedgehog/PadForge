"""The rules a translation of the interface follows, shared by check_translation.py and
write_resx.py. TranslationRulesTests holds every Strings.<tag>.resx in the tree to the
same rules on each build.
"""
import html
import io
import os
import re
import unicodedata

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
STRINGS = os.path.join(REPO, "PadForge.App", "Resources", "Strings")

ENTRY = re.compile(r'<data name="([^"]+)"[^>]*>\s*<value>(.*?)</value>', re.S)
PLACEHOLDER = re.compile(r"\{[^{}]*\}")
NUMBERED = re.compile(r"\{\d+(?::[^{}]*)?\}")
DECIMAL = re.compile(r"(?<![\w.])\d+\.\d+(?![\d.])")
INVISIBLE = {0x00AD, 0x034F, 0x061C, 0x115F, 0x1160, 0x180E, 0x200B, 0x200E, 0x200F, 0x2060, 0x2061, 0x2062,
             0x2063, 0x2064, 0x202A, 0x202B, 0x202C, 0x202D, 0x202E, 0x2066, 0x2067, 0x2068, 0x2069, 0x3164,
             0xFEFF, 0xFFA0, 0xFFF9, 0xFFFA, 0xFFFB} | set(range(0xFE00, 0xFE10))
SPACES = {0x00A0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009,
          0x200A, 0x202F, 0x205F, 0x3000}
SCRIPT_WORDS = {
    "Latn": ("LATIN",), "Cyrl": ("CYRILLIC",), "Grek": ("GREEK",), "Arab": ("ARABIC",), "Ethi": ("ETHIOPIC",),
    "Beng": ("BENGALI",), "Deva": ("DEVANAGARI",), "Gujr": ("GUJARATI",), "Knda": ("KANNADA",),
    "Mlym": ("MALAYALAM",), "Sinh": ("SINHALA",), "Taml": ("TAMIL",), "Telu": ("TELUGU",), "Thai": ("THAI",),
    "Laoo": ("LAO",), "Khmr": ("KHMER",), "Mymr": ("MYANMAR",), "Geor": ("GEORGIAN",), "Armn": ("ARMENIAN",),
    "Kore": ("HANGUL", "CJK"), "Jpan": ("HIRAGANA", "KATAKANA", "CJK"), "Hans": ("CJK",), "Hant": ("CJK",),
}
SENTENCE_END = set(".!?。।॥۔։။።។؟")
# The projects whose credits the app's tests read: the name, then the license in ASCII parentheses.
CREDITED = {"Dolphin", "new-lg4ff", "OpenLogi", "t19xx_usb", "ttyT1941", "hmkconf", "android-bluez-ime", "Linux",
            "zeemoted", "dji-firmware-tools", "NintendoExtensionCtrl", "spacenavd", "chmfp", "fortius_1942", "x360ce"}
LICENSE_TAG = re.compile(r"(\S+) \(([^()]*)\)")
# The About page's license chips that are a license's identifier or name, which AboutPageTests holds to the
# English in every language.
LEGAL_CHIPS = ("About_License_Mit", "About_License_Apache2", "About_License_Bsd3", "About_License_Bsd2",
               "About_License_Bsl1", "About_License_Isc", "About_License_Mpl2", "About_License_Unlicense",
               "About_License_Zlib", "About_License_Gpl2", "About_License_Gpl2OrLater", "About_License_Gpl3",
               "About_License_Gpl3OrLater", "About_License_Agpl3", "About_License_Lgpl21OrLater", "About_License_Lgpl3",
               "About_License_Lgpl3OrLater", "About_License_CcByNcSa4", "About_License_CcBy3", "About_License_CgTrader",
               "About_License_WyvrnEula")
PURE_LICENSE = re.compile(r"^(?:[A-Z0-9][\w.+/-]*|and)(?: (?:[A-Z0-9][\w.+/-]*|and))*$")


def read_resx(path):
    """key -> text of a .resx file, in file order."""
    text = io.open(path, encoding="utf-8-sig").read()
    return {k: html.unescape(v) for k, v in ENTRY.findall(text)}


def read_lines(name):
    path = os.path.join(HERE, name)
    return [l.strip() for l in io.open(path, encoding="utf-8-sig").read().splitlines() if l.strip() and not l.startswith("#")]


def languages():
    """tag -> (English name, own name, direction, script, coverage) from Languages.tsv."""
    rows = {}
    for line in io.open(os.path.join(STRINGS, "Languages.tsv"), encoding="utf-8-sig").read().splitlines():
        if line and not line.startswith("#"):
            cells = line.split("\t")
            rows[cells[0]] = tuple(cells[1:])
    return rows


def name_in(text, name):
    plural = "s?" if (name[-1].isupper() or name[-1].isdigit()) else ""
    return re.search(r"(?<![A-Za-z0-9])" + re.escape(name) + plural + r"(?![A-Za-z0-9])", text) is not None


def has_number(text, number):
    return re.search(r"(?<![\d.,])" + re.escape(number) + r"(?![\d]|[.,]\d)", text) is not None


def letters_of(ch, words):
    try:
        name = unicodedata.name(ch)
    except ValueError:
        return False
    return any(w in name for w in words)


def shared_closing_sentence(a, b):
    """The closing sentence a and b share, in any script. Mirrors
    DirectPathSeamTests.SharedClosingSentence."""
    a, b = a.rstrip(), b.rstrip()
    n = 0
    while n < min(len(a), len(b)) and a[len(a) - 1 - n] == b[len(b) - 1 - n]:
        n += 1
    marked = any(c in SENTENCE_END for c in a[:-1])

    def starts(text, at):
        if at == 0:
            return True
        before = text[at - 1]
        if before == "。":
            return True
        if not before.isspace():
            return False
        return (not marked) or (at >= 2 and text[at - 2] in SENTENCE_END)

    p = 0
    while p < n:
        i = len(a) - n + p
        if not a[i].isspace() and starts(a, i) and starts(b, len(b) - n + p):
            return a[i:]
        p += 1
    return ""


def check(english, translated, script):
    """Every way the translated strings break the rules, as (kind, key, message)."""
    names = read_lines("names.txt")
    words = SCRIPT_WORDS.get(script, ("LATIN",))
    problems = []
    letters_total = letters_in = 0
    for key, text in translated.items():
        if key not in english:
            problems.append(("KEY", key, "Strings.resx has no such key"))
            continue
        source = english[key]
        if text == "" and source != "":
            problems.append(("EMPTY", key, "an empty string. Leave the key out to show the English."))
            continue
        if set(NUMBERED.findall(source)) != set(NUMBERED.findall(text)):
            problems.append(("PLACEHOLDER", key, "the English has %s, the translation %s" % (
                sorted(set(NUMBERED.findall(source))), sorted(set(NUMBERED.findall(text))))))
        if PLACEHOLDER.sub("", source).count("{") + PLACEHOLDER.sub("", source).count("}") != \
                PLACEHOLDER.sub("", text).count("{") + PLACEHOLDER.sub("", text).count("}"):
            problems.append(("BRACE", key, "a brace outside a placeholder"))
        if source.count("\n") != text.count("\n"):
            problems.append(("LINES", key, "the English has %d line breaks, the translation %d" % (source.count("\n"), text.count("\n"))))
        bare_source, bare_text = PLACEHOLDER.sub(" ", source), PLACEHOLDER.sub(" ", text)
        for name in names:
            if name in bare_source and name_in(bare_source, name) and not name_in(bare_text, name):
                problems.append(("NAME", key, "keep the name %s exactly as the English writes it" % name))
        for number in DECIMAL.findall(bare_source):
            if not has_number(bare_text, number):
                problems.append(("DECIMAL", key, "write %s with a period, as the number boxes show and read it" % number))
        for i, ch in enumerate(text):
            cp = ord(ch)
            if cp in (0x200C, 0x200D):
                before = text[i - 1] if i > 0 else ""
                after = text[i + 1] if i + 1 < len(text) else ""
                joinable = all(c and (c.isalpha() or unicodedata.category(c) in ("Mn", "Mc")) for c in (before, after))
                if not joinable:
                    problems.append(("INVISIBLE", key, "U+%04X outside a word" % cp))
            elif cp in INVISIBLE or cp in SPACES:
                problems.append(("INVISIBLE", key, "U+%04X in the text" % cp))
            elif unicodedata.category(ch) == "Nd" and not ("0" <= ch <= "9"):
                problems.append(("DIGIT", key, "write digits as 0 to 9"))
            elif unicodedata.category(ch) == "Cc" and ch != "\n":
                problems.append(("CONTROL", key, "the control character U+%04X" % cp))
        if key.startswith("About_"):
            for project, grant in LICENSE_TAG.findall(source):
                project = project.rstrip(",")
                if project in CREDITED and PURE_LICENSE.match(grant):
                    parts = [re.escape(p) for p in grant.split(" and ")]
                    if not re.search(re.escape(project) + r"\s?\(" + r"[^)]*".join(parts) + r"\)", text):
                        problems.append(("CREDIT", key, "write %s (%s): the name unchanged, the license in ASCII parentheses" % (project, grant)))
        plain = bare_text
        for name in names:
            if len(name) > 1:
                plain = plain.replace(name, " ")
        for ch in plain:
            if unicodedata.category(ch).startswith("L"):
                letters_total += 1
                letters_in += letters_of(ch, words)
    if translated and letters_total and letters_in / letters_total < (0.80 if script == "Latn" else 0.55):
        problems.append(("SCRIPT", "-", "only %.0f%% of the letters are in the %s script" % (100.0 * letters_in / letters_total, script)))

    def shown(key):
        return translated.get(key, english[key])

    def agree(key, message):
        problems.append(("AGREE", key, message))

    if shown("GetProfiles_Title") not in shown("Workshop_UpdatesBody"):
        agree("Workshop_UpdatesBody", "must contain the exact text of GetProfiles_Title")
    if shown("ControllerType_KeyboardMouse") not in shown("Workshop_Becomes_Tip"):
        agree("Workshop_Becomes_Tip", "must name the type with the exact text of ControllerType_KeyboardMouse")
    for name in ("PadForge", "reWASD", "DS4Windows"):
        if name not in shown("Profiles_DropTileFormats"):
            agree("Profiles_DropTileFormats", "must keep " + name + " as written")
    for option in ("Pad_Lighting_ClaimLevel_Colors", "Pad_Lighting_ClaimLevel_Power"):
        if shown(option) not in shown("Pad_Lighting_ClaimLevel_Tooltip"):
            agree("Pad_Lighting_ClaimLevel_Tooltip", "must contain the exact text of " + option)
    if len(shown("Pad_Lighting_RouteWindowsLightingBuiltIn")) <= len(shown("Pad_Lighting_RouteWindowsLighting")):
        agree("Pad_Lighting_RouteWindowsLightingBuiltIn", "must be longer than Pad_Lighting_RouteWindowsLighting")
    if "25:26" not in shown("About_TestimonyRef") or shown("About_TestimonyRef") in shown("About_TestimonyScripture"):
        agree("About_TestimonyRef", "is the verse reference alone, with 25:26, and the verse does not repeat it")
    if "ComeUntoChrist.org" not in shown("About_TestimonyInvite"):
        agree("About_TestimonyInvite", "must keep ComeUntoChrist.org")
    confirm = shown("Devices_DriverConfirm_TouchSenseMouse")
    if not (10 < len(shared_closing_sentence(shown("Devices_DriverConfirm_Prodikeys"), confirm)) < len(confirm)):
        agree("Devices_DriverConfirm_TouchSenseMouse", "must end with the closing sentence of Devices_DriverConfirm_Prodikeys")
    if shown("ControllerType_KeyboardMouse") not in confirm:
        agree("Devices_DriverConfirm_TouchSenseMouse", "must name the type with the exact text of ControllerType_KeyboardMouse")
    # The button that opens Controller Presets reads the dialog's title, as the English does.
    if "Pad_ExtendedImportFromDevice" in translated and \
            shown("Pad_ExtendedImportFromDevice") != shown("ManageProfiles_Title") + "\u2026":
        agree("Pad_ExtendedImportFromDevice", "must be the exact text of ManageProfiles_Title followed by \u2026")
    if shown("Pad_Kbm_Surfaces_Both_Name") != shown("ControllerType_KeyboardMouse"):
        agree("Pad_Kbm_Surfaces_Both_Name", "must be the exact text of ControllerType_KeyboardMouse")
    for key in ("Settings_GKeysStatus_NoKeysYet", "Settings_GKeysTooltip"):
        if len(re.findall(r"G-key(?!s)", shown(key))) < len(re.findall(r"G-key(?!s)", english[key])):
            agree(key, "must name the Logitech Gaming Software command G-key as the English does")
    if not re.search(r"t19xx_usb\s?\(GPL-3\.0\)(, |、|، )ttyT1941\s?\(GPL-3\.0\)", shown("About_SpecialtyUsbRefsDesc")):
        agree("About_SpecialtyUsbRefsDesc", "must name ttyT1941 (GPL-3.0) right after t19xx_usb (GPL-3.0)")
    if shown("About_PeripheralOutputRefsDesc").count("OpenLogi") != 2:
        agree("About_PeripheralOutputRefsDesc", "must name OpenLogi twice, the project and its docs")
    for key in LEGAL_CHIPS:
        if key in english and shown(key) != english[key]:
            agree(key, "names a license: write it exactly as the English does")
    return problems
