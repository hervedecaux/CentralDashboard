#!/usr/bin/env bash
# Récupère la dernière version du code (git pull), la republie et redémarre l'application.
# Vos données ne sont pas touchées ; les migrations de la base s'appliquent au redémarrage.
#
# Usage : scripts/macos/mettre-a-jour.sh
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_commun.sh"

cd "$REPO_DIR"
BRANCHE="$(git rev-parse --abbrev-ref HEAD)"
if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then
    erreur "Le dépôt contient des modifications non enregistrées : mise à jour annulée."
    exit 1
fi
info "Récupération de la dernière version ($BRANCHE)…"
git pull --ff-only
ok "Code à jour : $(git log -1 --format='%h %s')"

exec "$(dirname "${BASH_SOURCE[0]}")/installer.sh" --sans-navigateur
