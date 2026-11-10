#!/bin/sh
# Exécuté sur la cible (via deploy.ps1) : installe les binaires déjà extraits dans le dossier donné en argument.
set -eu
SRC="$1"

getent group ursanas >/dev/null || groupadd --system ursanas
getent passwd ursanas >/dev/null || \
  useradd --system --gid ursanas --home-dir /var/lib/ursanas --shell /usr/sbin/nologin ursanas

# NFS et FTP : paquets installés une fois, services laissés arrêtés jusqu'à leur activation dans l'interface (page Services).
# btrfs-progs et exfatprogs : formatage des disques depuis la page Disques (Btrfs, exFAT).
# cifs-utils et nfs-common : montage des stockages distants SMB et NFS (page Stockage distant).
for entry in nfs-kernel-server:nfs-server vsftpd:vsftpd openssl: btrfs-progs: exfatprogs: cifs-utils: nfs-common:; do
  pkg="${entry%%:*}"; unit="${entry#*:}"
  if ! dpkg -s "$pkg" >/dev/null 2>&1; then
    if DEBIAN_FRONTEND=noninteractive apt-get install -y "$pkg" >/dev/null 2>&1; then
      [ -z "$unit" ] || systemctl disable --now "$unit" >/dev/null 2>&1 || true
    else
      echo "Avertissement : installation de $pkg impossible (réseau ?)" >&2
    fi
  fi
done
mkdir -p /etc/exports.d
# FTP s'authentifie avec les comptes du NAS, sans les contrôles de shell de la configuration PAM par défaut de vsftpd.
printf 'auth    required pam_unix.so
account required pam_unix.so
' > /etc/pam.d/ursanas-ftp

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
