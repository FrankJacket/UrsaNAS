#!/bin/sh
# Exécuté sur la cible (via deploy.ps1) : installe les binaires déjà extraits dans le dossier donné en argument.
set -eu
SRC="$1"

getent group ursanas >/dev/null || groupadd --system ursanas
getent passwd ursanas >/dev/null || \
  useradd --system --gid ursanas --home-dir /var/lib/ursanas --shell /usr/sbin/nologin ursanas

# NFS et FTP : paquets installés une fois, services laissés arrêtés jusqu'à leur activation dans l'interface (page Services).
# btrfs-progs et exfatprogs : formatage des disques depuis la page Disques (Btrfs, exFAT).
# cifs-utils et nfs-common : montage des stockages distants SMB et NFS ; fuse3 : OneDrive et Dropbox (page Stockage distant) ; curl : téléchargement de rclone.
for entry in nfs-kernel-server:nfs-server vsftpd:vsftpd openssl: btrfs-progs: exfatprogs: cifs-utils: nfs-common: fuse3: curl:; do
  pkg="${entry%%:*}"; unit="${entry#*:}"
  if ! dpkg -s "$pkg" >/dev/null 2>&1; then
    if DEBIAN_FRONTEND=noninteractive apt-get install -y "$pkg" >/dev/null 2>&1; then
      [ -z "$unit" ] || systemctl disable --now "$unit" >/dev/null 2>&1 || true
    else
      echo "Avertissement : installation de $pkg impossible (réseau ?)" >&2
    fi
  fi
done
# rclone (OneDrive, Dropbox) : la version des dépôts Ubuntu (1.60) envoie son propre secret d'application avec celui de l'utilisateur et ne peut donc pas
# renouveler les jetons de nos connexions. On installe la version officielle ci-dessous, dont l'empreinte est vérifiée (SHA256SUMS de cette version).
RCLONE_VERSION=v1.76.0
ARCH=$(dpkg --print-architecture)
case "$ARCH" in
  amd64) RCLONE_SHA=9db7ee9b725bce7cdd195b9183449a4c6fb93cad3e6578e70b87c0bc7f6062e9 ;;
  arm64) RCLONE_SHA=c486d404ecbc4bb3d54052a0efaf9e652cf0d2c645e4523f7a2aefa112fe029c ;;
  *) RCLONE_SHA= ;;
esac
if [ -n "$RCLONE_SHA" ] && [ "$(rclone version 2>/dev/null | head -1)" != "rclone $RCLONE_VERSION" ]; then
  T=$(mktemp -d); F="$T/rclone.deb"
  if curl -fsSL -m 300 -o "$F" "https://downloads.rclone.org/$RCLONE_VERSION/rclone-$RCLONE_VERSION-linux-$ARCH.deb" \
     && echo "$RCLONE_SHA  $F" | sha256sum -c - >/dev/null 2>&1 \
     && DEBIAN_FRONTEND=noninteractive apt-get install -y "$F" >/dev/null 2>&1; then
    systemctl try-restart 'ursanas-rclone-*.service' >/dev/null 2>&1 || true # les montages en cours reprennent avec le nouveau programme
  else
    echo "Avertissement : installation de rclone $RCLONE_VERSION impossible (réseau ?)" >&2
  fi
  rm -rf "$T"
fi
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
