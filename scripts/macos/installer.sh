#!/usr/bin/env bash
# Publie l'application (version autonome, Release) dans ~/Applications/DashboardPerso
# et la lance automatiquement à l'ouverture de session, en arrière-plan.
# Relancer ce script met à jour l'installation avec le code actuel du dépôt.
#
# Usage : scripts/macos/installer.sh [--sans-navigateur]
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_commun.sh"

OUVRIR=1
[[ "${1:-}" == "--sans-navigateur" ]] && OUVRIR=0

if [[ "$(uname -s)" != "Darwin" ]]; then
    erreur "Ce script est prévu pour macOS."
    exit 1
fi
if ! command -v dotnet >/dev/null 2>&1; then
    erreur "La commande dotnet est introuvable : installez le SDK .NET 10 (voir README)."
    exit 1
fi

# 1. Publication dans un dossier temporaire : l'installation en place n'est remplacée qu'en cas de succès.
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT
info "Publication pour $RID (quelques dizaines de secondes)…"
dotnet publish "$PROJECT_DIR" -c Release -r "$RID" --self-contained -o "$TMP_DIR/app" -v quiet -nologo
ok "Application publiée."

# 2. Arrêt de la version en cours, puis remplacement des fichiers.
arreter_agent
sleep 1
if lsof -nP -iTCP:5080 -sTCP:LISTEN >/dev/null 2>&1; then
    erreur "Le port 5080 est déjà utilisé (une version lancée avec « dotnet run » ?). Arrêtez-la puis relancez ce script."
    exit 1
fi
mkdir -p "$(dirname "$APP_DIR")" "$LOG_DIR" "$(dirname "$PLIST")"
rm -rf "$APP_DIR"
mv "$TMP_DIR/app" "$APP_DIR"
ok "Installée dans $APP_DIR"

# 3. Lancement automatique à l'ouverture de session (LaunchAgent), toujours relancée si elle s'arrête.
# ProcessType Interactive : avec Background, macOS bride le processeur et les disques et le démarrage peut prendre plusieurs minutes.
cat > "$PLIST" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>$LABEL</string>
    <key>ProgramArguments</key>
    <array>
        <string>$APP_DIR/DashboardPerso</string>
    </array>
    <key>WorkingDirectory</key>
    <string>$APP_DIR</string>
    <key>EnvironmentVariables</key>
    <dict>
        <key>ASPNETCORE_ENVIRONMENT</key>
        <string>Production</string>
    </dict>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>ThrottleInterval</key>
    <integer>10</integer>
    <key>ProcessType</key>
    <string>Interactive</string>
    <key>StandardOutPath</key>
    <string>$LOG_DIR/dashboard.log</string>
    <key>StandardErrorPath</key>
    <string>$LOG_DIR/dashboard-erreurs.log</string>
</dict>
</plist>
PLIST
plutil -lint "$PLIST" >/dev/null
launchctl bootstrap "gui/$(id -u)" "$PLIST"
launchctl enable "gui/$(id -u)/$LABEL"
ok "Lancement automatique à l'ouverture de session activé."
echo "  Si macOS affiche « Éléments d'arrière-plan ajoutés », laissez DashboardPerso autorisé dans"
echo "  Réglages Système › Général › Ouverture › Autoriser en arrière-plan : sinon il ne démarrera pas après un redémarrage."

# 4. Attente du démarrage.
info "Démarrage…"
for _ in $(seq 1 30); do
    if curl -fs -o /dev/null "$URL"; then
        ok "Dashboard disponible sur $URL"
        [[ $OUVRIR -eq 1 ]] && open "$URL"
        echo "  Données : $DATA_DIR"
        echo "  Journaux : $LOG_DIR"
        exit 0
    fi
    sleep 1
done
erreur "L'application ne répond pas encore. Lancez scripts/macos/diagnostic.sh pour en savoir plus."
exit 1
