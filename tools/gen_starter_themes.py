"""Builds the ten starter themes inside PadForge (#591).

Each starter is a theme file, PadForge.App/Resources/Themes/<slug>.pftheme: a zip that holds
theme.json and picture.png, the same file Settings' Save a Copy writes and Add Theme lists.

usage:
    python tools/gen_starter_themes.py                 write theme.json again, keep each picture
    python tools/gen_starter_themes.py --pictures DIR  take each picture from DIR/<slug>.png

A picture is 2352 by 480 pixels with a clear ground: 1176 by 240 units at twice the size, drawn
behind the top of the page. The pictures were made from the sources named in each theme's credits
(the LICENSE file lists them too). The zips are written with a fixed date, so a run that changes
nothing writes the same bytes.
"""
import io
import json
import os
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, 'PadForge.App', 'Resources', 'Themes')

# slug, name, look, base, background, accent, secondary, attention, tile, credits
# tile: where the gallery tile's window into the picture starts, as a share of its width.
STARTERS = [
    ('sonic-blue-blur', 'Sonic the Hedgehog', 'Blue Blur', 'dark', '#07114A', '#FFC700', '#9FD3FF', '#FF8A3D', 0.36,
     'Picture: Sonic on Kronos Island, a frame of the Sonic Frontiers trailer from SEGA\'s own YouTube channel, '
     'CC BY 3.0, found on Wikimedia Commons. The Sonic the Hedgehog logo is a trademark of SEGA.'),
    ('sonic-night-run', 'Sonic the Hedgehog', 'Night Run', 'dark', '#060B26', '#2B7BFF', '#FFD23F', '#FF9A3D', 0.33,
     'Picture: Sonic running, SEGA\'s art made for Sonic Lost World, found on Sonic Wiki Zone. '
     'Sonic and the Sonic the Hedgehog logo belong to SEGA.'),
    ('rings-the-one-ring', 'The Lord of the Rings', 'The One Ring', 'dark', '#0E0A06', '#F5B041', '#C9D6E8', '#FF8A4D', 0.36,
     'Picture: the Hobbiton film set in New Zealand, a photo by Jackie.lck, CC BY 2.0, found on Wikimedia Commons. '
     'The Lord of the Rings logo is a trademark of its owner.'),
    ('rings-map-of-middle-earth', 'The Lord of the Rings', 'Map of Middle-earth', 'light', '#E9DDC3', '#A23B2A', '#2F5D7C', '#8A6A1F', 0.32,
     'Picture: a map of Middle-earth, found on The One Wiki to Rule Them All, which names no artist. '
     'The Lord of the Rings logo is a trademark of its owner.'),
    ('potter-gryffindor', 'Harry Potter', 'Gryffindor', 'dark', '#170609', '#EEBA30', '#E8D9B0', '#FF7A5C', 0.28,
     'Picture: Hogwarts castle, a frame of the Hogwarts Legacy trailer from the game\'s own YouTube channel, '
     'CC BY 3.0, found on Wikimedia Commons. The Harry Potter wordmark is a trademark of Warner Bros.'),
    ('potter-four-houses', 'Harry Potter', 'Four Houses', 'dark', '#090C22', '#E0344A', '#7FA8FF', '#FFD84D', 0.3,
     'Picture: the four Hogwarts house crests, drawn by Atomhawk Design for Pottermore, found on the Harry Potter Wiki. '
     'The crests and the Harry Potter wordmark belong to Warner Bros.'),
    ('starwars-opening-crawl', 'Star Wars', 'Opening Crawl', 'dark', '#02040A', '#FFE81F', '#4BD5EE', '#FF8A3D', 0.36,
     'Picture: the full-size Millennium Falcon at Galaxy\'s Edge, a photo by Tomás Del Coro, CC BY-SA 2.0, '
     'found on Wikimedia Commons. The Star Wars logo is a trademark of Lucasfilm.'),
    ('starwars-dark-side', 'Star Wars', 'Dark Side', 'dark', '#070303', '#FF2B2B', '#C9D1DA', '#FFB347', 0.3,
     'Picture: Darth Vader\'s helmet, a Lucasfilm promotional photo for Revenge of the Sith, found on Wookieepedia. '
     'Darth Vader and the Star Wars logo belong to Lucasfilm.'),
    ('startrek-lcars', 'Star Trek', 'LCARS', 'dark', '#000000', '#FF9900', '#BBA6FF', '#FFEE99', 0.33,
     'Picture: the studio model of the USS Enterprise filmed for the original series, photographed by the Smithsonian '
     'National Air and Space Museum, which holds it (inventory A19740668000). The Star Trek logo is a trademark of '
     'its owner.'),
    ('startrek-starfleet', 'Star Trek', 'Starfleet', 'dark', '#0C1014', '#F2C230', '#5FA4FF', '#FF6F59', 0.35,
     'Picture: the Enterprise-D, a frame of Star Trek: The Next Generation, found on Memory Alpha. '
     'Star Trek, the Enterprise and the series logo belong to their owner.'),
]

PICTURE = 'picture.png'
FIXED_DATE = (1980, 1, 1, 0, 0, 0)


def manifest(name, look, base, background, accent, secondary, attention, tile, credits):
    # The key order ThemeFile.ManifestBytes writes.
    data = {
        'name': name,
        'look': look,
        'author': 'PadForge',
        'base': base,
        'colors': {'background': background, 'accent': accent, 'secondary': secondary, 'attention': attention},
        'picture': PICTURE,
        'tile': tile,
        'credits': credits,
    }
    return (json.dumps(data, indent=2, ensure_ascii=False) + '\n').encode('utf-8')


def kept_picture(path):
    if not os.path.exists(path):
        return None
    with zipfile.ZipFile(path) as old:
        return old.read(PICTURE)


def main():
    pictures = None
    if '--pictures' in sys.argv:
        pictures = sys.argv[sys.argv.index('--pictures') + 1]
    os.makedirs(OUT, exist_ok=True)
    for slug, name, look, base, background, accent, secondary, attention, tile, credits in STARTERS:
        path = os.path.join(OUT, slug + '.pftheme')
        if pictures:
            with open(os.path.join(pictures, slug + '.png'), 'rb') as f:
                picture = f.read()
        else:
            picture = kept_picture(path)
        if picture is None:
            sys.exit(slug + ' has no picture: run with --pictures DIR')
        if picture[:8] != bytes((0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)):
            sys.exit(slug + ': the picture is not a PNG')
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, 'w') as package:
            entry = zipfile.ZipInfo('theme.json', FIXED_DATE)
            entry.compress_type = zipfile.ZIP_DEFLATED
            package.writestr(entry, manifest(name, look, base, background, accent, secondary, attention, tile, credits))
            entry = zipfile.ZipInfo(PICTURE, FIXED_DATE)
            entry.compress_type = zipfile.ZIP_STORED
            package.writestr(entry, picture)
        with open(path, 'wb') as f:
            f.write(buffer.getvalue())
        print('%-28s %7d bytes' % (slug + '.pftheme', len(buffer.getvalue())))


if __name__ == '__main__':
    main()
