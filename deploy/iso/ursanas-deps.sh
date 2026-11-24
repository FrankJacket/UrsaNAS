#!/bin/sh
# Installs the UrsaNAS dependencies that need more care than the installer's package list: ZFS, Docker, virtual
# machines (KVM / libvirt) and rclone. Run by late.sh inside the new system, at the end of the Linux installation,
# one stage at a time so that late.sh can show the progress:   ursanas-deps.sh zfs | docker | kvm | rclone
#
# Same packages and same rules as the dependency part of install.sh (keep both in sync). Nothing here is fatal: a
# package that cannot be installed is reported and skipped, and install.sh installs what is missing at the first start.
export DEBIAN_FRONTEND=noninteractive

ARCH=amd64
LOG=/var/log/ursanas-deps.log
UPDATED=/tmp/ursanas-apt-updated   # the package lists are updated once for all the stages

# Each step is stamped with the time: it shows which one is slow.
say()  { printf '\n==> [%s] %s\n' "$(date +%H:%M:%S)" "$*" | tee -a "$LOG"; }
warn() { printf 'WARNING: [%s] %s\n' "$(date +%H:%M:%S)" "$*" | tee -a "$LOG" >&2; }

# The installer disables the "deb cdrom:" source only when it is over: until then the medium cannot be read in this
# environment and apt-get update fails, without the lists of the other sources. Switch it off here.
[ -f /etc/apt/sources.list ] && sed -i 's/^deb cdrom:/#deb cdrom:/' /etc/apt/sources.list

APT_UPDATED=0
[ -f "$UPDATED" ] && APT_UPDATED=1
apt_update() {
    [ "$APT_UPDATED" -eq 1 ] && return 0
    say "Updating package lists"
    # Twice at most; the messages of apt-get go to the log, to find out why when it fails.
    if apt-get update -qq >> "$LOG" 2>&1 || { sleep 5; apt-get update -qq >> "$LOG" 2>&1; }; then
        : > "$UPDATED"
    else
        warn "apt-get update failed (see the messages above in this log)"
    fi
    APT_UPDATED=1
}
installed() { dpkg -s "$1" > /dev/null 2>&1; }

# Installs the packages that are missing; returns non-zero if one of them could not be installed.
apt_install() {
    missing=
    for p in "$@"; do installed "$p" || missing="$missing $p"; done
    [ -n "$missing" ] || return 0
    apt_update
    say "Installing:$missing"
    # shellcheck disable=SC2086
    apt-get install -y -qq $missing >> "$LOG" 2>&1 && return 0
    rc=0
    for p in $missing; do apt-get install -y -qq "$p" >> "$LOG" 2>&1 || { warn "could not install $p"; rc=1; }; done
    return $rc
}

stage_zfs() {
    # ZFS lives in the "contrib" component of Debian.
    say "Installing ZFS"
    if [ -f /etc/apt/sources.list.d/debian.sources ]; then
        grep -qE '^Components:.*\bcontrib\b' /etc/apt/sources.list.d/debian.sources \
            || { sed -i '/^Components:/ s/$/ contrib/' /etc/apt/sources.list.d/debian.sources; rm -f "$UPDATED"; APT_UPDATED=0; }
    elif [ -f /etc/apt/sources.list ]; then
        grep -qE '^deb .*\bcontrib\b' /etc/apt/sources.list \
            || { sed -E -i '/^deb /{/ contrib/! s/ main/ main contrib/}' /etc/apt/sources.list; rm -f "$UPDATED"; APT_UPDATED=0; }
    fi
    apt_install "linux-headers-$ARCH" zfs-dkms zfsutils-linux || warn "ZFS could not be installed: pools and datasets will be unavailable"
}

stage_docker() {
    say "Installing Docker"
    apt_install docker.io || warn "Docker could not be installed"
    command -v docker > /dev/null 2>&1 && { docker compose version > /dev/null 2>&1 || apt_install docker-compose || apt_install docker-compose-v2 || warn "Docker Compose could not be installed (stacks will be unavailable)"; }
}

stage_kvm() {
    say "Installing virtual machine support (KVM / libvirt)"
    apt_install qemu-system-x86 libvirt-daemon-system libvirt-clients virtinst ovmf swtpm || warn "libvirt could not be installed"
}

stage_rclone() {
    # The official release, checksum verified, as install.sh does. late.sh read the version and the checksum
    # from the install.sh of the UrsaNAS package.
    [ -f /tmp/ursanas-rclone.env ] || return 0
    . /tmp/ursanas-rclone.env
    [ -n "${RCLONE_VERSION:-}" ] && [ -n "${RCLONE_SHA:-}" ] || return 0
    say "Installing rclone $RCLONE_VERSION"
    F=/tmp/rclone.deb
    if curl -fsSL -m 300 -o "$F" "https://downloads.rclone.org/$RCLONE_VERSION/rclone-$RCLONE_VERSION-linux-$ARCH.deb" \
       && echo "$RCLONE_SHA  $F" | sha256sum -c - > /dev/null 2>&1 \
       && apt-get install -y -qq "$F" >> "$LOG" 2>&1; then
        :
    else
        warn "rclone $RCLONE_VERSION could not be installed (network?): OneDrive and Dropbox will be unavailable"
    fi
    rm -f "$F"
}

case "${1:-all}" in
    zfs)    stage_zfs ;;
    docker) stage_docker ;;
    kvm)    stage_kvm ;;
    rclone) stage_rclone ;;
    all)    stage_zfs; stage_docker; stage_kvm; stage_rclone ;;
esac
# The last stage removes the marker of the package lists.
case "${1:-all}" in rclone | all) rm -f "$UPDATED" ;; esac
exit 0
