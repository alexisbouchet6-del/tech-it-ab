#!/usr/bin/env python3
# mkapp.py <App> <Lettres> <couleur hex> "<texte accueil>" [admin|user]  — icône + installateur (by alex.b)
import os, sys
from PIL import Image, ImageDraw, ImageFont
app, letters, col, welcome = sys.argv[1:5]
root = os.path.dirname(os.path.abspath(__file__))
os.makedirs(f"{root}/{app}/build", exist_ok=True); os.makedirs(f"{root}/{app}/installer", exist_ok=True); os.makedirs(f"{root}/{app}/src", exist_ok=True)
c = tuple(int(col[i:i+2], 16) for i in (0, 2, 4))
S = 256; im = Image.new("RGBA", (S, S), (0, 0, 0, 0)); d = ImageDraw.Draw(im)
d.rounded_rectangle((8, 8, S-8, S-8), 52, fill=(9, 13, 22, 255), outline=c + (255,), width=14)
d.rounded_rectangle((30, 30, S-30, S-30), 36, outline=(255, 43, 214, 120), width=4)
fp = "/usr/share/fonts/truetype/dejavu/DejaVuSansMono-Bold.ttf"
f = ImageFont.truetype(fp, 118 if len(letters) <= 2 else 84)
w, h = d.textbbox((0, 0), letters, font=f)[2:]
d.text(((S - w) / 2, (S - h) / 2 - 10), letters, font=f, fill=c + (255,))
im.save(f"{root}/{app}/build/app.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
t = open(f"{root}/TestReseau/installer/TestReseau.nsi", encoding="utf-8").read()
t = t.replace("TestReseau", app)
import re
W = '!define MUI_WELCOMEPAGE_TEXT "' + welcome + r'$\r$\n$\r$\nDéveloppé by alex.b.$\r$\n$\r$\nPrérequis : Windows 10 / 11 (.NET Framework 4.8 inclus)."'
t = re.sub(r'!define MUI_WELCOMEPAGE_TEXT "[^\n]*', lambda m: W, t)
t = t.replace("; " + app + " - installateur", "; " + app + " - installateur")
open(f"{root}/{app}/installer/{app}.nsi", "w", encoding="utf-8").write(t)
print("ok", app)
