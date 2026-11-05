#!/bin/sh
# Exécuté sur la cible (via deploy.ps1) : installe les binaires déjà extraits dans le dossier donné en argument.
set -eu
SRC="$1"

getent group ursanas >/dev/null || groupadd --system ursanas
getent passwd ursanas >/dev/null || \
  useradd --system --gid ursanas --home-dir /var/lib/ursanas --shell /usr/sbin/nologin ursanas

mkdir -p /opt/ursanas/agent /opt/ursanas/web
rm -rf /opt/ursanas/web/*

systemctl stop ursanas-web ursanas-agent 2>/dev/null || true
cp -rf "$SRC"/Nas.Agent/. /opt/ursanas/agent/
cp -rf "$SRC"/Nas.Web/. /opt/ursanas/web/
chmod 755 /opt/ursanas/agent/Nas.Agent /opt/ursanas/web/Nas.Web
cp -f "$SRC"/ursanas-agent.service "$SRC"/ursanas-web.service /etc/systemd/system/

systemctl daemon-reload
systemctl enable ursanas-agent ursanas-web >/dev/null 2>&1
systemctl restart ursanas-agent ursanas-web
sleep 2
systemctl is-active ursanas-agent ursanas-web
