"""Builds the self-hosted Chinese display font: Noto Serif SC cut down to the characters the site uses.

The site shows Noto Serif SC only for decorative Chinese text (tiles, pieces, game names, faan names,
headings on the rules pages), and all of that text is in the source. So the subset holds every Chinese
character found in the .cs and .razor files and the translation dictionaries, plus Latin letters and
digits. Characters outside it (say, in a player's nickname) fall back to the system serif font.

Usage, from the repository root, after adding Chinese text anywhere in the site:

    python GameWeb/Fonts/subset_fonts.py <path to NotoSerifSC variable .ttf>

Writes GameWeb/wwwroot/fonts/noto-serif-sc-600.woff and -900.woff. Needs fontTools (pip install fonttools).
"""
import glob
import os
import re
import sys

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))   # the GameWeb project folder
OUT = os.path.join(ROOT, 'wwwroot', 'fonts')
CJK = re.compile(r'[　-〿一-鿿＀-￯]')


def site_characters() -> str:
    files = [f for pattern in ('**/*.cs', '**/*.razor', 'Resources/translations.*.py')
             for f in glob.glob(os.path.join(ROOT, pattern), recursive=True)
             if os.sep + 'obj' + os.sep not in f and os.sep + 'bin' + os.sep not in f]
    chars = set()
    for f in files:
        with open(f, encoding='utf-8-sig') as fh:
            chars |= set(CJK.findall(fh.read()))
    # Latin letters, digits and punctuation, for mixed text like "1 番".
    chars |= {chr(c) for c in range(0x20, 0x7f)}
    return ''.join(sorted(chars))


def build(source: str, weight: int, text: str) -> None:
    font = TTFont(source)
    if 'fvar' in font:
        font = instancer.instantiateVariableFont(font, {'wght': weight})
    options = subset.Options()
    options.flavor = 'woff'
    options.layout_features = ['*']
    options.name_IDs = ['*']
    options.name_languages = ['*']
    options.notdef_outline = True
    subsetter = subset.Subsetter(options)
    subsetter.populate(text=text)
    subsetter.subset(font)
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, f'noto-serif-sc-{weight}.woff')
    font.flavor = 'woff'
    font.save(path)
    print(f'{path}: {os.path.getsize(path) // 1024} KB')


if __name__ == '__main__':
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    text = site_characters()
    print(f'{len(text)} characters')
    for weight in (600, 900):
        build(sys.argv[1], weight, text)
