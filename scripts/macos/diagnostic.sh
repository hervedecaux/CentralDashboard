#!/usr/bin/env bash
# Vérifie pourquoi le dashboard installé ne tourne pas, sans rien modifier.
# Usage : scripts/macos/diagnostic.sh
set -uo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_commun.sh"

DOMAINE="gui/$(id -u)"

echo "== Fichiers"
if [[ -x "$APP_DIR/DashboardPerso" ]]; then ok "Application : $APP_DIR/DashboardPerso"
else erreur "Application absente ou non exécutable : $APP_DIR/DashboardPerso (relancez installer.sh)"; fi
if [[ -f "$PLIST" ]]; then
    if plutil -lint "$PLIST" >/dev/null; then ok "Lancement automatique : $PLIST"
    else erreur "Fichier de lancement invalide : $PLIST (relancez installer.sh)"; fi
else
    erreur "Fichier de lancement absent : $PLIST (relancez installer.sh)"
fi

echo
echo "== launchd"
if launchctl print-disabled "$DOMAINE" 2>/dev/null | grep -Eq "\"$LABEL\" => (true|disabled)"; then
    erreur "Le lancement automatique est désactivé. Réactivez-le avec : launchctl enable $DOMAINE/$LABEL"
fi
if INFO="$(launchctl print "$DOMAINE/$LABEL" 2>/dev/null)"; then
    printf '%s\n' "$INFO" | grep -E '^[[:space:]]*(state|pid|runs|last exit code|last terminating signal) =' | sed 's/^[[:space:]]*/  /'
else
    erreur "L'agent $LABEL n'est pas chargé dans votre session."
    echo "  Cause la plus fréquente : il est désactivé dans Réglages Système › Général › Ouverture"
    echo "  (section « Autoriser en arrière-plan »). Activez-le, puis relancez scripts/macos/installer.sh."
fi

echo
echo "== Réponse sur $URL"
if curl -fs -o /dev/null --max-time 3 "$URL"; then ok "Le dashboard répond."
else
    erreur "Pas de réponse."
    if lsof -nP -iTCP:5080 -sTCP:LISTEN 2>/dev/null | tail -n +2 | grep -q .; then
        echo "  Le port 5080 est occupé par :"
        lsof -nP -iTCP:5080 -sTCP:LISTEN | tail -n +2 | sed 's/^/  /'
    fi
fi

echo
echo "== Dernières erreurs ($LOG_DIR/dashboard-erreurs.log)"
if [[ -s "$LOG_DIR/dashboard-erreurs.log" ]]; then tail -n 30 "$LOG_DIR/dashboard-erreurs.log" | sed 's/^/  /'
else echo "  (aucune)"; fi
echo
echo "== Fin du journal ($LOG_DIR/dashboard.log)"
if [[ -s "$LOG_DIR/dashboard.log" ]]; then tail -n 15 "$LOG_DIR/dashboard.log" | sed 's/^/  /'
else echo "  (vide)"; fi
