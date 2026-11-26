#!/bin/bash
# AMI·OPS : compile l'appli + toutes les applis de la suite (attributs d'assembly retirés des sources embarquées)
# Développé by alex.b — usage : bash suite/AmiOps/build-amiops.sh
set -euo pipefail
cd "$(dirname "$0")/.."
API=/usr/lib/mono/4.8-api
LEG=../; [ -d ../legacy ] && LEG=../legacy/
EXT=$(mktemp -d /tmp/amiops-ext.XXXXXX); trap 'rm -rf "$EXT"' EXIT
for a in DiagPoste MaintenancePC PrepaPoste ProfilSave TestReseau ScanParc MailCheck TriFichiers RapportInter NasConnect; do
  for f in "$a"/src/*.cs; do grep -v '^\[assembly:' "$f" > "$EXT/${a}_$(basename "$f")"; done
done
for d in netswitch outils-vtx powermenu; do
  for f in "$LEG$d"/src/*.cs; do grep -v '^\[assembly:' "$f" > "$EXT/${d}_$(basename "$f")"; done
done
grep -v '^\[assembly:' "${LEG}common-legacy/Harden.cs" > "$EXT/legacy_Harden.cs"
REFS=()
for r in mscorlib System System.Core System.Drawing System.Windows.Forms System.Management System.Web.Extensions System.Security; do REFS+=("-r:$API/$r.dll"); done
mcs -nologo -target:winexe -platform:anycpu -optimize+ -langversion:7 -nostdlib -nowarn:649,414,169,219 "${REFS[@]}" -main:AmiOps.Program \
  -win32icon:AmiOps/build/app.ico -out:AmiOps/build/AmiOps.exe common/Cyber.cs AmiOps/src/*.cs "$EXT"/*.cs
echo "OK : AmiOps/build/AmiOps.exe"
