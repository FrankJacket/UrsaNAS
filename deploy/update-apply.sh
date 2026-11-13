#!/bin/bash
# Appelé par l'agent (détaché, via systemd-run) pour appliquer une mise à jour d'UrsaNAS déjà téléchargée et vérifiée.
# $1 : dossier extrait de l'archive de la release (contient install.sh, VERSION, Nas.Agent/, Nas.Web/…)
# $2 : fichier d'état lu par l'agent (et par l'interface) pour suivre la progression ; survit au redémarrage de l'agent.
#
# install.sh redémarre ursanas-agent : ce script tourne dans sa propre unité systemd transitoire, il n'est donc pas
# interrompu par ce redémarrage. S'il échoue ou si les services ne répondent pas, la version précédente est restaurée.
set -u

SRC="$1"
STATUS="$2"
BACKUP="/opt/ursanas.prev"
VERSION="$(cat "$SRC/VERSION" 2>/dev/null || echo "")"

write() { # stage message
  printf '{"stage":"%s","version":"%s","message":"%s","updatedAt":"%s"}\n' \
    "$1" "$VERSION" "$2" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >"$STATUS"
}

write running "Backing up the current version"
rm -rf "$BACKUP"
mkdir -p "$BACKUP"
[ -d /opt/ursanas/agent ] && cp -a /opt/ursanas/agent "$BACKUP/"
[ -d /opt/ursanas/web ] && cp -a /opt/ursanas/web "$BACKUP/"
[ -f /opt/ursanas/VERSION ] && cp -a /opt/ursanas/VERSION "$BACKUP/"

write running "Installing the update"
if ! bash "$SRC/install.sh" >>"$STATUS.log" 2>&1; then
  write failed "Installation failed: see the installer log on the server"
  exit 1
fi

write running "Checking the services"
i=0
while [ "$i" -lt 30 ]; do
  if systemctl is-active --quiet ursanas-agent && systemctl is-active --quiet ursanas-web; then
    write done "Update installed"
    exit 0
  fi
  sleep 1
  i=$((i + 1))
done

write running "Restoring the previous version"
systemctl stop ursanas-web ursanas-agent 2>/dev/null
rm -rf /opt/ursanas/agent /opt/ursanas/web
[ -d "$BACKUP/agent" ] && cp -a "$BACKUP/agent" /opt/ursanas/
[ -d "$BACKUP/web" ] && cp -a "$BACKUP/web" /opt/ursanas/
[ -f "$BACKUP/VERSION" ] && cp -a "$BACKUP/VERSION" /opt/ursanas/
systemctl daemon-reload
systemctl restart ursanas-agent ursanas-web
write rolled_back "The services did not respond: the previous version was restored"
exit 1
