"""Draw placeholder key text into the two empty API-key boxes of CENSORED.png.

Geometry, colour and font were measured from the screenshot itself:
  text colour  (151,125,145)      sampled from the existing field text
  font         Arial 74           best glyph-overlap match (IoU .395)
  inset        21px from box left, baseline at box centre +25
"""
from PIL import Image, ImageDraw, ImageFont

SRC = "CENSORED.png"
OUT = "gallery-ai-setup.png"
GALLERY_WIDTH = 1920

FONT = r"C:\Windows\Fonts\arial.ttf"
SIZE = 74
INK = (151, 125, 145)
HINT = (176, 158, 174)

# measured empty boxes: (left, right, centre_y)
BOXES = {
    "openrouter": (466, 2659, 1051),
    "xai": (462, 1943, 1513),
}

FIELDS = [
    ("openrouter", "sk-or-v1-", "your OpenRouter key"),
    ("xai", "xai-", "your xAI key"),
]


def fit_bullets(font, left, right, prefix, hint):
    """Widest run of bullets that still leaves room for the hint label."""
    gap = font.getlength("    ")
    avail = (right - 21) - (left + 21)
    for n in range(40, 3, -1):
        text = prefix + "\u2022" * n
        if font.getlength(text) + gap + font.getlength(hint) <= avail:
            return text, gap
    return prefix, gap


def main():
    im = Image.open(SRC).convert("RGB")
    d = ImageDraw.Draw(im)
    font = ImageFont.truetype(FONT, SIZE)

    for key, prefix, hint in FIELDS:
        left, right, cy = BOXES[key]
        x = left + 21
        baseline = cy + 25
        text, gap = fit_bullets(font, left, right, prefix, hint)
        d.text((x, baseline), text, font=font, fill=INK, anchor="ls")
        d.text((x + font.getlength(text) + gap, baseline), hint,
               font=font, fill=HINT, anchor="ls")
        print(f"{key:11s} '{text[:14]}...' + '{hint}'  at x={x} baseline={baseline}")

    w, h = im.size
    out = im.resize((GALLERY_WIDTH, round(h * GALLERY_WIDTH / w)), Image.LANCZOS)
    out.save(OUT, optimize=True)
    print(f"\nwrote {OUT}  {out.size[0]}x{out.size[1]}")


if __name__ == "__main__":
    main()
