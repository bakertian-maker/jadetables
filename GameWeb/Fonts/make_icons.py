"""Draws the site's bitmap icons and the link-preview image from the self-hosted fonts.

    python GameWeb/Fonts/make_icons.py

Writes to GameWeb/wwwroot:
  favicon.ico           16, 32 and 48 px (browsers and search engines ask for /favicon.ico)
  apple-touch-icon.png  180 px, for "Add to Home Screen" on phones
  og-image.png          1200 x 630, the picture shown when a link is shared (WhatsApp, WeChat, Facebook…)
Needs Pillow and fontTools (pip install pillow fonttools).
"""
import io
import os

from fontTools.ttLib import TTFont
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WWW = os.path.join(ROOT, 'wwwroot')
FONTS = os.path.join(WWW, 'fonts')

JADE_900, JADE_700 = (11, 46, 38), (21, 82, 67)
IVORY, IVORY_SHADOW, RED, GOLD = (251, 248, 239), (217, 207, 180), (194, 59, 34), (242, 201, 76)


def font(woff_name: str, size: int) -> ImageFont.FreeTypeFont:
    """Pillow reads TrueType, so convert the site's WOFF file in memory."""
    f = TTFont(os.path.join(FONTS, woff_name))
    f.flavor = None
    buf = io.BytesIO()
    f.save(buf)
    buf.seek(0)
    return ImageFont.truetype(buf, size)


def tile(size: int, char: str = '中') -> Image.Image:
    """The brand tile: an ivory mahjong tile with a red character, on a transparent background."""
    scale = 4   # draw large, then shrink, for smooth edges
    s = size * scale
    img = Image.new('RGBA', (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    w, h = s * 0.74, s * 0.94
    x0, y0 = (s - w) / 2, (s - h) / 2
    r = s * 0.12
    d.rounded_rectangle((x0, y0 + s * 0.05, x0 + w, y0 + h), r, fill=IVORY_SHADOW)
    d.rounded_rectangle((x0, y0, x0 + w, y0 + h - s * 0.05), r, fill=IVORY)
    f = font('noto-serif-sc-900.woff', int(s * 0.58))
    d.text((s / 2, y0 + (h - s * 0.05) / 2), char, font=f, fill=RED, anchor='mm')
    return img.resize((size, size), Image.Resampling.LANCZOS if hasattr(Image, "Resampling") else Image.LANCZOS)


def favicon() -> None:
    big = tile(256)
    big.save(os.path.join(WWW, 'favicon.ico'), sizes=[(16, 16), (32, 32), (48, 48)])


def apple_touch_icon() -> None:
    img = Image.new('RGB', (180, 180), JADE_700)
    t = tile(150)
    img.paste(t, (15, 15), t)
    img.save(os.path.join(WWW, 'apple-touch-icon.png'), optimize=True)


def og_image() -> None:
    W, H = 1200, 630
    img = Image.new('RGB', (W, H), JADE_900)
    d = ImageDraw.Draw(img)
    # Soft diagonal light, like the site's hero.
    for y in range(H):
        t = y / H
        c = tuple(int(JADE_700[i] * (1 - t) + JADE_900[i] * t) for i in range(3))
        d.line([(0, y), (W, y)], fill=c)
    # A fan of tiles on the right.
    for i, ch in enumerate(['中', '東', '象', '棋']):
        t = tile(175, ch).rotate(12 - i * 8, resample=Image.Resampling.BICUBIC if hasattr(Image, "Resampling") else Image.BICUBIC, expand=True)
        img.paste(t, (650 + i * 112, int(200 + abs(i - 1.5) * 26)), t)
    sans = lambda size, weight=700: _outfit(size, weight)
    d.text((80, 150), 'Jade Tables', font=sans(92), fill=IVORY)
    d.text((84, 262), '玉桌', font=font('noto-serif-sc-900.woff', 64), fill=GOLD)
    d.text((80, 372), 'Free Mahjong & Xiangqi online', font=sans(42, 500), fill=(207, 216, 210))
    d.text((80, 428), 'No download · no account · multiplayer', font=sans(34, 400), fill=(169, 181, 174))
    d.text((80, 540), 'www.jadetables.com', font=sans(30, 500), fill=GOLD)
    img.save(os.path.join(WWW, 'og-image.png'), optimize=True)


def _outfit(size: int, weight: int) -> ImageFont.FreeTypeFont:
    f = font('outfit-variable.woff', size)
    try:
        f.set_variation_by_axes([weight])
    except Exception:
        pass   # an older FreeType without variable-font support: the default weight is fine
    return f


if __name__ == '__main__':
    favicon()
    apple_touch_icon()
    og_image()
    for name in ('favicon.ico', 'apple-touch-icon.png', 'og-image.png'):
        print(name, os.path.getsize(os.path.join(WWW, name)) // 1024, 'KB')
