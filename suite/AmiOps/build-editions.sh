#!/bin/bash
# Compile les deux éditions (Mono) — Développé by alex.b
#   Tech IT AB  : console complète + labo cyber (usage technicien)
#   PC Santé AB : symbole POSTE, compilé SANS le code sensible (Nmap, clients, parc, GLPI, coffre, Multi-PC, AD, secours)
# usage : bash suite/AmiOps/build-editions.sh
set -euo pipefail
cd "$(dirname "$0")/.."
API=/usr/lib/mono/4.8-api
LEG=../; [ -d ../legacy ] && LEG=../legacy/
EXT=$(mktemp -d /tmp/techitab-ext.XXXXXX); trap 'rm -rf "$EXT"' EXIT
for a in DiagPoste MaintenancePC PrepaPoste ProfilSave TestReseau ScanParc MailCheck TriFichiers RapportInter NasConnect; do
  for f in "$a"/src/*.cs; do grep -v '^\[assembly:' "$f" > "$EXT/${a}_$(basename "$f")"; done
done
for d in netswitch outils-vtx powermenu; do
  for f in "$LEG$d"/src/*.cs; do grep -v '^\[assembly:' "$f" > "$EXT/${d}_$(basename "$f")"; done
done
grep -v '^\[assembly:' "${LEG}common-legacy/Harden.cs" > "$EXT/legacy_Harden.cs"
REFS=()
for r in mscorlib System System.Core System.Drawing System.Windows.Forms System.Management System.Web.Extensions System.Security System.Xml; do REFS+=("-r:$API/$r.dll"); done
OPTS=(-nologo -target:winexe -platform:anycpu -optimize+ -langversion:7 -nostdlib "-nowarn:649,414,169,219" -main:AmiOps.Program)

# Tech IT AB : tout
mcs "${OPTS[@]}" "${REFS[@]}" -win32icon:AmiOps/build/techitab.ico -out:AmiOps/build/TechITAB.exe common/Cyber.cs AmiOps/src/*.cs "$EXT"/*.cs
echo "OK : AmiOps/build/TechITAB.exe"

# PC Santé AB : sous-ensemble explicite (liste blanche) + symbole POSTE
POSTE_SRC=()
for f in AmiOps/src/*.cs; do case "$(basename "$f")" in Pages.cs|Parc.cs|CyberPage.cs|Labo.cs|Remote.cs|Secours.cs) ;; *) POSTE_SRC+=("$f");; esac; done
POSTE_EXT=()
for f in "$EXT"/*.cs; do case "$(basename "$f")" in DiagPoste_*|MaintenancePC_*|TestReseau_*|ScanParc_*|TriFichiers_*|MailCheck_*|netswitch_*|outils-vtx_*|powermenu_*|legacy_*) POSTE_EXT+=("$f");; esac; done
mcs "${OPTS[@]}" -define:POSTE "${REFS[@]}" -win32icon:AmiOps/build/pcsante.ico -out:AmiOps/build/PCSanteAB.exe common/Cyber.cs "${POSTE_SRC[@]}" "${POSTE_EXT[@]}"
echo "OK : AmiOps/build/PCSanteAB.exe"
