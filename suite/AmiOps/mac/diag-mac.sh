#!/bin/bash
# AMI·OPS - diagnostic macOS (lecture seule) - Développé by alex.b
# Usage : Terminal → bash diag-mac.sh   (sudo bash diag-mac.sh pour FileVault et le pare-feu détaillés)
# Rapport : ~/Desktop/Diag-Mac_<nom>_<date>.txt
# shellcheck disable=SC2015  # « cond && warn || ok » volontaire : ok/warn/info (printf) renvoient toujours 0

OUT="$HOME/Desktop/Diag-Mac_$(scutil --get ComputerName 2>/dev/null | tr ' /' '__')_$(date +%Y-%m-%d_%H%M).txt"
ok()   { printf "  [OK]    %s\n" "$1"; }
warn() { printf "  [ALERTE] %s\n" "$1"; }
info() { printf "  %s\n" "$1"; }
sec()  { printf "\n// %s\n" "$1"; }

{
echo "DIAGNOSTIC MAC — $(scutil --get ComputerName 2>/dev/null) — $(date '+%d/%m/%Y %H:%M') (AMI·OPS, by alex.b)"

sec "SYSTÈME"
info "macOS $(sw_vers -productVersion) (build $(sw_vers -buildVersion))"
HW=$(system_profiler SPHardwareDataType 2>/dev/null)
info "$(echo "$HW" | awk -F': ' '/Model Name|Nom du modèle/{print $2; exit}') — $(echo "$HW" | awk -F': ' '/Model Identifier|Identifiant du modèle/{print $2; exit}')"
info "Puce / processeur : $(echo "$HW" | awk -F': ' '/Chip|Puce|Processor Name|Nom du processeur/{print $2; exit}')"
info "N° de série : $(echo "$HW" | awk -F': ' '/Serial Number|Numéro de série/{print $2; exit}')"
info "Démarré depuis : $(uptime | sed 's/.*up \([^,]*\),.*/\1/')"
MAJOR=$(sw_vers -productVersion | cut -d. -f1)
[ "$MAJOR" -lt 13 ] && warn "macOS $MAJOR : plus de mises à jour de sécurité (Apple suit en général les 3 dernières versions)" || ok "version de macOS récente"

sec "MÉMOIRE"
info "RAM : $(( $(sysctl -n hw.memsize) / 1073741824 )) Go"
PRESS=$(memory_pressure 2>/dev/null | awk -F': ' '/System-wide memory free percentage/{print $2}')
[ -n "$PRESS" ] && { P=${PRESS%\%}; [ "$P" -lt 15 ] && warn "mémoire libre $PRESS : saturation (fermer des applis, ou Mac sous-dimensionné)" || ok "mémoire libre $PRESS"; }
info "Applis les plus gourmandes :"; ps -Ao rss,comm -m | sed -n '2,6p' | awk '{printf "    %5d Mo  %s\n", $1/1024, $2}'

sec "DISQUE"
DF=$(df -H / | tail -1); USED=$(echo "$DF" | awk '{print $5}' | tr -d '%')
info "Disque système : $(echo "$DF" | awk '{print $4}') libres sur $(echo "$DF" | awk '{print $2}')"
[ "$USED" -gt 90 ] && warn "disque plein à $USED % : macOS ralentit et les mises à jour échouent" || ok "espace disque correct ($USED % utilisés)"
SMART=$(diskutil info disk0 2>/dev/null | awk -F': *' '/SMART Status|État SMART/{print $2}')
[ -n "$SMART" ] && { [[ "$SMART" == Verified* || "$SMART" == Vérifié* ]] && ok "SMART : $SMART" || warn "SMART : $SMART → sauvegarder et remplacer"; }

sec "BATTERIE"
PW=$(system_profiler SPPowerDataType 2>/dev/null)
CYC=$(echo "$PW" | awk -F': ' '/Cycle Count|Nombre de cycles/{print $2; exit}')
COND=$(echo "$PW" | awk -F': ' '/Condition|État/{print $2; exit}')
MAXC=$(echo "$PW" | awk -F': ' '/Maximum Capacity|Capacité maximale/{print $2; exit}')
if [ -n "$CYC" ]; then
  info "Cycles : $CYC · état : $COND · capacité max : ${MAXC:-?}"
  [[ "$COND" =~ (Normal|Normale) ]] && ok "batterie en bon état" || warn "batterie à remplacer ($COND)"
else info "pas de batterie (Mac de bureau)"; fi

sec "SÉCURITÉ"
FV=$(fdesetup status 2>/dev/null); [[ "$FV" == *"On"* ]] && ok "FileVault activé" || warn "FileVault désactivé : disque non chiffré (Réglages > Confidentialité et sécurité)"
GK=$(spctl --status 2>/dev/null); [[ "$GK" == *"enabled"* ]] && ok "Gatekeeper actif" || warn "Gatekeeper désactivé : n'importe quelle appli peut s'exécuter"
SIP=$(csrutil status 2>/dev/null); [[ "$SIP" == *"enabled"* ]] && ok "Protection de l'intégrité du système (SIP) active" || warn "SIP désactivé"
FW=$(/usr/libexec/ApplicationFirewall/socketfilterfw --getglobalstate 2>/dev/null); [[ "$FW" == *"enabled"* || "$FW" == *"State = 1"* ]] && ok "pare-feu actif" || warn "pare-feu désactivé (Réglages > Réseau > Coupe-feu)"
info "Administrateurs : $(dscl . -read /Groups/admin GroupMembership 2>/dev/null | cut -d: -f2)"

sec "DÉMARRAGE (agents et daemons tiers)"
for d in "$HOME/Library/LaunchAgents" /Library/LaunchAgents /Library/LaunchDaemons; do
  [ -d "$d" ] || continue
  for f in "$d"/*.plist; do [ -e "$f" ] || continue; n=$(basename "$f"); [[ "$n" == com.apple.* ]] && continue; info "$d/$n"; done
done
info "Éléments d'ouverture : Réglages > Général > Ouverture"

sec "MISES À JOUR"
SU=$(softwareupdate -l 2>&1)
echo "$SU" | grep -q "No new software" && ok "macOS à jour" || { warn "mises à jour disponibles :"; echo "$SU" | grep -E '^\s*\*' | sed 's/^/    /'; }

sec "SAUVEGARDE TIME MACHINE"
TM=$(tmutil latestbackup 2>/dev/null)
[ -n "$TM" ] && info "dernière sauvegarde : $(basename "$TM")" || warn "aucune sauvegarde Time Machine trouvée"

sec "RÉSEAU"
WIF=$(networksetup -listallhardwareports | awk '/Wi-Fi/{getline; print $2}')
info "Wi-Fi ($WIF) : IP $(ipconfig getifaddr "$WIF" 2>/dev/null || echo '-')"
info "Ethernet : IP $(ipconfig getifaddr en0 2>/dev/null || echo '-')"
info "Passerelle : $(route -n get default 2>/dev/null | awk '/gateway/{print $2}') · DNS : $(scutil --dns | awk '/nameserver\[0\]/{print $3; exit}')"
ping -c 3 -q 1.1.1.1 >/dev/null 2>&1 && ok "Internet joignable" || warn "Internet injoignable"

echo; echo "Fin du diagnostic — rapport : $OUT"
} | tee "$OUT"
