#!/usr/bin/env python3
# package.py <App> <version> [aperçu.png]  — installateur NSIS + dossier zip (by alex.b)
import os, re, shutil, subprocess, sys, zipfile
app, ver = sys.argv[1], sys.argv[2]
preview = sys.argv[3] if len(sys.argv) > 3 else None
root = os.path.dirname(os.path.abspath(__file__))
nsi = os.path.join(root, app, "installer", app + ".nsi")
s = open(nsi, encoding="utf-8").read()
s = re.sub(r'!define VERSION\s+"[^"]+"', f'!define VERSION   "{ver}"', s)
s = re.sub(r'VIProductVersion "[^"]+"', f'VIProductVersion "{ver}.0"', s)
open(nsi, "w", encoding="utf-8").write(s)
os.makedirs(os.path.join(root, app, "dist"), exist_ok=True)
subprocess.run(["makensis", "-V2", app + ".nsi"], cwd=os.path.dirname(nsi), check=True)
out = "/home/claude/livraison/suite"
d = os.path.join(out, app)
shutil.rmtree(d, ignore_errors=True); os.makedirs(d)
shutil.copy(os.path.join(root, app, "dist", f"{app}-Setup-{ver}.exe"), d)
shutil.copy(os.path.join(root, app, "build", app + ".exe"), os.path.join(d, app + "-portable.exe"))
if preview and os.path.exists(preview): shutil.copy(preview, os.path.join(d, "apercu.png"))
readme = os.path.join(root, app, "LISEZMOI.txt")
if os.path.exists(readme):
    txt = open(readme, encoding="utf-8").read().replace("{VERSION}", ver)
    open(os.path.join(d, "LISEZMOI.txt"), "w", encoding="utf-8", newline="\r\n").write(txt)
z = f"/home/claude/livraison/{app}-{ver}.zip"
if os.path.exists(z): os.remove(z)
with zipfile.ZipFile(z, "w", zipfile.ZIP_DEFLATED) as zf:
    for f in sorted(os.listdir(d)): zf.write(os.path.join(d, f), os.path.join(app, f))
print(z, os.path.getsize(z))
