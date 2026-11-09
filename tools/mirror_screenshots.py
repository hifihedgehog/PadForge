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

import io
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
    "screen-color": "pad-lighting-screen-color",
    "twitch-chat": "dashboard-twitch-chat",
    "screen-overlay": "dashboard-screen-overlay",
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

    # The site asks for assets index.html has not got yet. The loop below
    # walks the assets that already EXIST, so a capture whose shot landed in
    # wiki/images but that the site has never had an asset for is invisible
    # to it: the figure stays commented out waiting for a file nothing will
    # ever create. Take index.html as the authority on what the site wants,
    # comments included, and create anything with a source behind it.
    wanted = set()
    index_html = os.path.join(SITE, "index.html")
    if os.path.exists(index_html):
        import re
        with io.open(index_html, encoding="utf-8") as fh:
            wanted = set(re.findall(r'assets/(screenshot-[A-Za-z0-9._-]+)\.jpg', fh.read()))
    created, wanted_without_source = [], []
    for name in sorted(wanted):
        if os.path.exists(os.path.join(SITE_ASSETS, name + ".jpg")):
            continue
        src = source_for(name[len("screenshot-"):])
        if src is None:
            # The page asks for it and nothing on disk can make it. That is
            # a picture the site is missing, so it is said out loud below.
            if name[len("screenshot-"):] not in NO_SOURCE_OK:
                wanted_without_source.append(name)
            continue
        Image.open(src).convert("RGB").save(
            os.path.join(SITE_ASSETS, name + ".jpg"), "JPEG", quality=88, optimize=True)
        created.append(name)
    if created:
        print("site assets CREATED from a source the site had no asset for:", len(created))
        for c in created:
            print("   %s.jpg" % c)

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

    # The same gap from the other side: the page names an asset, nothing on
    # disk can build it, and no asset exists yet for the loop above to trip on.
    if wanted_without_source:
        print()
        print("FAIL: index.html names assets with no source PNG:", len(wanted_without_source))
        for w in wanted_without_source:
            print("   %s.jpg" % w)
        return 1

    print("every site asset resolved to a source")
    return 0


if __name__ == "__main__":
    sys.exit(main())
