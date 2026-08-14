"""Replace the two live API keys in the AI Setup screenshot with placeholders."""
from PIL import Image, ImageDraw, ImageFont

SRC = r"C:\Users\canak\OneDrive\Pictures\Screenshots\Screenshot 2026-08-07 191515.png"
OUT = r"C:\Projects\AI2U-CustomAI\media\gallery-ai-setup.png"

# (x0, y0, x1, y1) interior of each key input box, measured from the source.
# The xAI box is clipped at x=2115 so the overlapping Test button stays intact.
FIELDS = [
    ((804, 1052, 2992, 1138), "sk-or-v1-\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022   your OpenRouter key"),
    ((800, 1745, 2285, 1862), "xai-\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022   your xAI key"),
]

FILL = (253, 253, 254)
INK = (125, 107, 140)

im = Image.open(SRC).convert("RGB")
d = ImageDraw.Draw(im)

try:
    font = ImageFont.truetype(r"C:\Windows\Fonts\arial.ttf", 54)
except OSError:
    font = ImageFont.load_default()

for (x0, y0, x1, y1), text in FIELDS:
    d.rectangle((x0, y0, x1, y1), fill=FILL)
    box = d.textbbox((0, 0), text, font=font)
    ty = y0 + ((y1 - y0) - (box[3] - box[1])) // 2 - box[1]
    d.text((x0 + 18, ty), text, font=font, fill=INK)

im.save(OUT)
print("wrote", OUT, im.size)
