"""Mirror captured screenshots into the repo and the website, and FAIL on a gap.

Every stale picture this project has shipped came from a mirror step that
silently skipped something. First it was the PNG-to-JPG `$map` that had no
entry for a new shot. Then it was a loop that only refreshed assets named in
index.html, which left four macro shots showing an empty editor for weeks.
Then it was this script's predecessor printing "no PNG source" for four assets
and moving on, which left screenshot-guide-led, screenshot-nintendo and
screenshot-playstation stale on the live site.

The rule that replaces all of that: every destination is refreshed from a
source, and any destination without one is an ERROR, not a note. A mirror that
cannot explain every file it manages is not finished.

Run from anywhere. Paths are resolved from this file's location.
"""

import os
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
PADFORGE = os.path.dirname(HERE)
SITE = os.path.join(os.path.dirname(PADFORGE), "padforge.org")
SRC = os.path.join(SITE, "wiki", "images")
REPO_SHOTS = os.path.join(PADFORGE, "screenshots")
SITE_ASSETS = os.path.join(SITE, "assets")

# Site asset base name -> source PNG base name, where they differ.
# A site asset whose name matches its PNG needs no entry.
ALIASES = {
    "guide-led": "pad-lighting-guide-led",
    "nintendo": "pad-nintendo-configbar",
    "playstation": "pad-playstation-configbar",
    "mouse-gestures": "pad-mouse-gestures",
    "controller": "pad-controller-3d",
    "extended": "pad-extended-schematic",
    "force-feedback": "pad-forcefeedback",
    "midi": "midi-input",
    "starter-profiles": "profiles-starter-gallery",
    "wii": "wii-pointer-mode",
    "bass-shakers": "pad-bass-shakers",
}

# Site assets that deliberately have no captured source (brand art, awards).
NO_SOURCE_OK = {
    "softpedia-excellent-editors-review-award",
}


def source_for(base):
    """Resolve a site-asset base name to its source PNG path, or None."""
    for cand in (ALIASES.get(base, base), base, "pad-" + base):
        p = os.path.join(SRC, cand + ".png")
        if os.path.exists(p):
            return p
    return None


def main():
    if not os.path.isdir(SRC):
        print("no source directory:", SRC)
        return 1

    pngs = sorted(f for f in os.listdir(SRC) if f.endswith(".png"))
    for f in pngs:
        base = f[:-4]
        Image.open(os.path.join(SRC, f)).convert("RGB").save(
            os.path.join(REPO_SHOTS, base + ".jpg"), "JPEG", quality=88, optimize=True)
    print("repo screenshots refreshed:", len(pngs))

    refreshed, unmapped = 0, []
    for jpg in sorted(f for f in os.listdir(SITE_ASSETS) if f.startswith("screenshot-") and f.endswith(".jpg")):
        base = jpg[len("screenshot-"):-4]
        if base in NO_SOURCE_OK:
            continue
        src = source_for(base)
        if src is None:
            unmapped.append(base)
            continue
        Image.open(src).convert("RGB").save(
            os.path.join(SITE_ASSETS, jpg), "JPEG", quality=88, optimize=True)
        refreshed += 1
    print("site assets refreshed:", refreshed)

    if unmapped:
        print()
        print("FAIL: site assets with no source PNG:", len(unmapped))
        for u in unmapped:
            print("   screenshot-%s.jpg" % u)
        print()
        print("Each one is either a capture that never ran, or a name this")
        print("script has no ALIAS for. Both are gaps. Fix the capture or add")
        print("the alias. Do not delete the asset to make this pass.")
        return 1

    print("every site asset resolved to a source")
    return 0


if __name__ == "__main__":
    sys.exit(main())
