# Réglages partagés par les scripts d'installation macOS (à ne pas lancer directement).
# shellcheck disable=SC2034  # variables utilisées par les scripts qui incluent ce fichier

# Dossier du dépôt (deux niveaux au-dessus de ce script).
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT_DIR="$REPO_DIR/src/DashboardPerso"

# Où l'application publiée est installée, et sous quel nom elle est lancée à l'ouverture de session.
APP_DIR="${DASHBOARD_APP_DIR:-$HOME/Applications/DashboardPerso}"
LABEL="local.dashboardperso"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
LOG_DIR="$HOME/Library/Logs/DashboardPerso"
URL="http://localhost:5080"
DATA_DIR="$HOME/Library/Application Support/DashboardPerso"

# Par sécurité, le dossier d'installation (qui est remplacé à chaque mise à jour) doit s'appeler DashboardPerso.
if [[ "$(basename "$APP_DIR")" != "DashboardPerso" ]]; then
    printf 'Dossier d'"'"'installation invalide : %s\n' "$APP_DIR" >&2
    exit 1
fi

# Processeur : Apple Silicon (arm64) ou Intel (x86_64).
case "$(uname -m)" in
    arm64) RID="osx-arm64" ;;
    *)     RID="osx-x64" ;;
esac
RID="${DASHBOARD_RID:-$RID}"

info()   { printf '\033[1;34m›\033[0m %s\n' "$*"; }
ok()     { printf '\033[1;32m✓\033[0m %s\n' "$*"; }
erreur() { printf '\033[1;31m✗\033[0m %s\n' "$*" >&2; }

# Arrête l'application lancée en arrière-plan, si elle tourne.
arreter_agent() {
    launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
}
