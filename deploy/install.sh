#!/bin/sh
# UrsaNAS installer / updater. Run as root on the NAS (Debian 13 or Ubuntu Server 26.04, x86-64).
#
#   curl -fsSL <raw URL of this script> | sudo sh -s -- [--with-docker] [--with-vm] [--no-zfs]   (downloads the newest release)
#   sudo ./install.sh [--with-docker] [--with-vm] [--no-zfs] [DIR]   (from an extracted package; DIR defaults to this script's folder)
#
# Running it again installs the new binaries over the old ones and keeps all data and settings.
set -eu

WITH_DOCKER=0; WITH_VM=0; WITH_ZFS=1; SRC=
for arg in "$@"; do
  case "$arg" in
    --with-docker) WITH_DOCKER=1 ;;
    --with-vm)     WITH_VM=1 ;;
    --no-zfs)      WITH_ZFS=0 ;;
    -h|--help)     sed -n '2,7p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*)            echo "Unknown option: $arg" >&2; exit 2 ;;
    *)             SRC="$arg" ;;
  esac
done
[ -n "$SRC" ] || SRC=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

say()  { printf '\n==> %s\n' "$*"; }
warn() { printf 'WARNING: %s\n' "$*" >&2; }
die()  { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------- checks
[ "$(id -u)" -eq 0 ] || die "run this script as root (use sudo)"

# ---------------------------------------------------------------- download (when run on its own, e.g. piped from curl)
# Without the program next to it, the script fetches the newest release (pre-releases included) and runs the installer it contains.
# For a private repository, set GITHUB_TOKEN to a token that can read the repository contents.
if [ ! -f "$SRC/Nas.Agent/Nas.Agent" ]; then
  REPO=FrankJacket/UrsaNAS
  fetch() { # fetch URL [OUTPUT]  (stdout when no output file)
    if command -v curl >/dev/null 2>&1; then
      curl -fsSL ${GITHUB_TOKEN:+-H "Authorization: Bearer $GITHUB_TOKEN"} -H "Accept: ${ACCEPT:-application/vnd.github+json}" ${2:+-o "$2"} "$1"
    elif command -v wget >/dev/null 2>&1; then
      wget -q ${GITHUB_TOKEN:+--header="Authorization: Bearer $GITHUB_TOKEN"} --header="Accept: ${ACCEPT:-application/vnd.github+json}" -O "${2:--}" "$1"
    else
      die "curl or wget is required to download UrsaNAS"
    fi
  }
  say "Looking for the newest UrsaNAS release"
  JSON=$(fetch "https://api.github.com/repos/$REPO/releases?per_page=1") \
    || die "cannot read the releases of $REPO (private repository? set GITHUB_TOKEN)"
  # Each asset lists its API "url" before its "name": remember the last asset url and print it when the name matches.
  asset_url() { printf '%s\n' "$JSON" | grep -oE '"(url|name)": *"[^"]*"' \
      | awk -F'"' -v pat="$1" '$2=="url" && $4 ~ /\/releases\/assets\/[0-9]+$/ {u=$4} $2=="name" && $4 ~ pat {print u; exit}'; }
  PKG_URL=$(asset_url '^ursanas-.*-linux-x64[.]tar[.]gz$')
  [ -n "$PKG_URL" ] || die "no UrsaNAS package found in the newest release of $REPO"
  SUM_URL=$(asset_url '^ursanas-.*-linux-x64[.]tar[.]gz[.]sha256$')
  TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
  say "Downloading"
  ACCEPT=application/octet-stream fetch "$PKG_URL" "$TMP/pkg.tar.gz" || die "download failed"
  if [ -n "$SUM_URL" ]; then
    ACCEPT=application/octet-stream fetch "$SUM_URL" "$TMP/pkg.sha256" || die "checksum download failed"
    [ "$(sha256sum "$TMP/pkg.tar.gz" | cut -d' ' -f1)" = "$(cut -d' ' -f1 "$TMP/pkg.sha256")" ] || die "checksum mismatch: the download is corrupted"
  fi
  tar -xzf "$TMP/pkg.tar.gz" -C "$TMP"
  INNER=$(ls -d "$TMP"/ursanas-*/ 2>/dev/null | head -1)
  [ -n "$INNER" ] && [ -f "${INNER}install.sh" ] || die "unexpected package layout"
  # Not exec: the trap above must remove the download folder afterwards.
  sh "${INNER}install.sh" "$@"
  exit $?
fi

[ -f "$SRC/Nas.Web/Nas.Web" ] || die "Nas.Web/ not found in $SRC"
command -v apt-get >/dev/null 2>&1 || die "this installer needs a Debian or Ubuntu system (apt-get not found)"
ARCH=$(dpkg --print-architecture)
[ "$ARCH" = amd64 ] || die "only x86-64 (amd64) is supported, this machine is $ARCH"
OS_ID=unknown; OS_NAME=
if [ -r /etc/os-release ]; then OS_ID=$(. /etc/os-release; echo "${ID:-unknown}"); OS_NAME=$(. /etc/os-release; echo "${PRETTY_NAME:-}"); fi
case "$OS_ID" in
  debian|ubuntu) ;;
  *) warn "untested distribution ($OS_NAME); continuing as if it were Debian" ;;
esac
export DEBIAN_FRONTEND=noninteractive

# ---------------------------------------------------------------- packages
APT_UPDATED=0
apt_update() { [ "$APT_UPDATED" -eq 1 ] || { say "Updating package lists"; apt-get update -qq || warn "apt-get update failed"; APT_UPDATED=1; }; }
installed() { dpkg -s "$1" >/dev/null 2>&1; }

# Installs the packages that are missing; returns non-zero if one of them could not be installed.
apt_install() {
  missing=
  for p in "$@"; do installed "$p" || missing="$missing $p"; done
  [ -n "$missing" ] || return 0
  apt_update
  say "Installing:$missing"
  # shellcheck disable=SC2086
  apt-get install -y -qq $missing && return 0
  rc=0
  for p in $missing; do apt-get install -y -qq "$p" || { warn "could not install $p"; rc=1; }; done
  return $rc
}

# Packages whose service must stay stopped until it is switched on from the Services page.
need_off() { installed "$1" || echo "$2"; }
OFF_UNITS="$(need_off nfs-kernel-server nfs-server) $(need_off vsftpd vsftpd)"

say "Installing required packages"
# samba, smbclient: file sharing and ACLs (smbcacls, pdbedit)      acl: getfacl/setfacl
# smartmontools: disk health                                         xfsprogs: grow an XFS system disk
# btrfs-progs, exfatprogs: format disks                              cifs-utils, nfs-common: remote storage (SMB, NFS)
# nfs-kernel-server, vsftpd: NFS and FTP sharing                     fuse3, curl: cloud storage (rclone)
apt_install ca-certificates curl openssl acl samba smbclient smartmontools xfsprogs btrfs-progs exfatprogs \
  cifs-utils nfs-common nfs-kernel-server vsftpd fuse3 || warn "some packages are missing: the matching features will be unavailable"
for u in $OFF_UNITS; do systemctl disable --now "$u" >/dev/null 2>&1 || true; done

# ---------------------------------------------------------------- ZFS (pools and datasets)
if [ "$WITH_ZFS" -eq 1 ] && ! command -v zpool >/dev/null 2>&1; then
  say "Installing ZFS (this can take several minutes)"
  if [ "$OS_ID" = debian ]; then
    # ZFS lives in the "contrib" component of Debian.
    if [ -f /etc/apt/sources.list.d/debian.sources ]; then
      grep -qE '^Components:.*\bcontrib\b' /etc/apt/sources.list.d/debian.sources \
        || { sed -i '/^Components:/ s/$/ contrib/' /etc/apt/sources.list.d/debian.sources; APT_UPDATED=0; }
    elif [ -f /etc/apt/sources.list ]; then
      grep -qE '^deb .*\bcontrib\b' /etc/apt/sources.list \
        || { sed -E -i '/^deb /{/ contrib/! s/ main/ main contrib/}' /etc/apt/sources.list; APT_UPDATED=0; }
    fi
    apt_install "linux-headers-$ARCH" zfs-dkms zfsutils-linux || warn "ZFS could not be installed: pools and datasets will be unavailable"
  else
    apt_install zfsutils-linux || warn "ZFS could not be installed: pools and datasets will be unavailable"
  fi
  modprobe zfs >/dev/null 2>&1 || warn "the ZFS module is not loaded yet; reboot the machine once the installation is over"
fi

# ---------------------------------------------------------------- optional features
if [ "$WITH_DOCKER" -eq 1 ]; then
  say "Installing Docker"
  apt_install docker.io || warn "Docker could not be installed"
  command -v docker >/dev/null 2>&1 && { docker compose version >/dev/null 2>&1 || apt_install docker-compose-v2 || apt_install docker-compose || warn "Docker Compose could not be installed (stacks will be unavailable)"; }
fi
if [ "$WITH_VM" -eq 1 ]; then
  say "Installing virtual machine support (KVM / libvirt)"
  apt_install qemu-system-x86 libvirt-daemon-system libvirt-clients virtinst ovmf swtpm || warn "libvirt could not be installed"
  [ -e /dev/kvm ] || warn "/dev/kvm is missing: enable virtualization in the BIOS (or nested virtualization if this machine is itself a VM)"
fi

# ---------------------------------------------------------------- rclone (OneDrive, Dropbox)
# The rclone shipped by Ubuntu (1.60) cannot renew the tokens of our connections: install the official release, checksum verified.
RCLONE_VERSION=v1.76.0
RCLONE_SHA=9db7ee9b725bce7cdd195b9183449a4c6fb93cad3e6578e70b87c0bc7f6062e9
if [ "$(rclone version 2>/dev/null | head -1)" != "rclone $RCLONE_VERSION" ]; then
  say "Installing rclone $RCLONE_VERSION"
  T=$(mktemp -d); F="$T/rclone.deb"
  if curl -fsSL -m 300 -o "$F" "https://downloads.rclone.org/$RCLONE_VERSION/rclone-$RCLONE_VERSION-linux-$ARCH.deb" \
     && echo "$RCLONE_SHA  $F" | sha256sum -c - >/dev/null 2>&1 \
     && apt-get install -y -qq "$F" >/dev/null; then
    systemctl try-restart 'ursanas-rclone-*.service' >/dev/null 2>&1 || true # running mounts pick up the new program
  else
    warn "rclone $RCLONE_VERSION could not be installed (network?): OneDrive and Dropbox will be unavailable"
  fi
  rm -rf "$T"
fi

# ---------------------------------------------------------------- UrsaNAS
say "Installing UrsaNAS"
getent group ursanas >/dev/null || groupadd --system ursanas
getent passwd ursanas >/dev/null || \
  useradd --system --gid ursanas --home-dir /var/lib/ursanas --shell /usr/sbin/nologin ursanas

mkdir -p /etc/exports.d
# FTP signs in with the NAS accounts, without the shell checks of vsftpd's default PAM configuration.
printf 'auth    required pam_unix.so\naccount required pam_unix.so\n' > /etc/pam.d/ursanas-ftp

mkdir -p /opt/ursanas/agent /opt/ursanas/web
rm -rf /opt/ursanas/web/*

systemctl stop ursanas-web ursanas-agent 2>/dev/null || true
cp -rf "$SRC"/Nas.Agent/. /opt/ursanas/agent/
cp -rf "$SRC"/Nas.Web/. /opt/ursanas/web/
chmod 755 /opt/ursanas/agent/Nas.Agent /opt/ursanas/web/Nas.Web
[ ! -f "$SRC/VERSION" ] || cp -f "$SRC/VERSION" /opt/ursanas/VERSION
cp -f "$SRC"/ursanas-agent.service "$SRC"/ursanas-web.service /etc/systemd/system/

systemctl daemon-reload
systemctl enable ursanas-agent ursanas-web >/dev/null 2>&1
systemctl restart ursanas-agent ursanas-web

# ---------------------------------------------------------------- check
say "Checking"
i=0
while [ "$i" -lt 30 ]; do
  curl -fsS -m 2 -o /dev/null http://127.0.0.1:8080/ 2>/dev/null && break
  i=$((i + 1)); sleep 1
done
if systemctl is-active --quiet ursanas-agent && systemctl is-active --quiet ursanas-web && [ "$i" -lt 30 ]; then
  IP=$(hostname -I 2>/dev/null | awk '{print $1}')
  VER=; [ ! -f /opt/ursanas/VERSION ] || VER=" $(cat /opt/ursanas/VERSION)"
  printf '\nUrsaNAS%s is running.\nOpen  http://%s:8080  in a browser and create the first administrator.\n' "$VER" "${IP:-<this-machine>}"
else
  echo "UrsaNAS did not start. Look at:  journalctl -u ursanas-agent -u ursanas-web -n 50" >&2
  exit 1
fi
