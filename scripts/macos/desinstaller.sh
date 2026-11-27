#!/usr/bin/env bash
# Arrête l'application, supprime le lancement automatique et le dossier ~/Applications/DashboardPerso.
# Vos données (base SQLite) sont conservées.
#
# Usage : scripts/macos/desinstaller.sh
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_commun.sh"

arreter_agent
rm -f "$PLIST"
ok "Lancement automatique supprimé."
if [[ -d "$APP_DIR" ]]; then
    rm -rf "$APP_DIR"
    ok "Dossier $APP_DIR supprimé."
fi
echo "  Vos données sont conservées dans : $DATA_DIR"
echo "  Les journaux sont dans : $LOG_DIR"
