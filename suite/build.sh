#!/bin/bash
# Compile une appli de la suite — Développé by alex.b
# usage : bash build.sh <App> [références supplémentaires…]   ex. bash build.sh DiagPoste System.Management
set -euo pipefail
cd "$(dirname "$0")"
APP=${1:?usage : build.sh <App> [refs…]}; shift
API=/usr/lib/mono/4.8-api
REFS=()
for r in mscorlib System System.Core System.Drawing System.Windows.Forms "$@"; do REFS+=("-r:$API/$r.dll"); done
mkdir -p "$APP/build"
mcs -nologo -target:winexe -platform:anycpu -optimize+ -langversion:7 -nostdlib -nowarn:649 "${REFS[@]}" \
  -win32icon:"$APP/build/app.ico" -out:"$APP/build/$APP.exe" common/Cyber.cs "$APP"/src/*.cs
echo "OK : $APP/build/$APP.exe"
