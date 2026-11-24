#!/bin/sh
# preseed/late_command: the system is installed in /target. Install the dependencies that need care (ZFS, Docker,
# virtual machines, rclone), then put the UrsaNAS package and the first-boot installer in place; the first boot of
# the new system installs UrsaNAS itself (see ursanas-firstboot.sh).
# A progress bar tells what is going on; nothing here stops the installation: what is missing is installed by
# install.sh at the first start. Log of the dependencies: /var/log/ursanas-deps.log.
set -e

. /usr/share/debconf/confmodule

T=/target
STEPS=6

step() { db_progress INFO "$1" || true; }
next() { db_progress STEP 1 || true; }

db_progress START 0 $STEPS ursanas/progress-title || true

# --- 1. what the next steps need
step ursanas/progress-prepare
mkdir -p "$T/opt/ursanas-setup"
cp /cdrom/ursanas/ursanas.tar.gz /cdrom/ursanas/ursanas.tar.gz.sha256 "$T/opt/ursanas-setup/"

# rclone: the version and checksum come from the install.sh of the UrsaNAS package.
inst=$(tar -xzO -f /cdrom/ursanas/ursanas.tar.gz --wildcards '*/install.sh' 2> /dev/null || true)
{
    printf 'RCLONE_VERSION=%s\n' "$(printf '%s\n' "$inst" | sed -n 's/^RCLONE_VERSION=//p' | head -n 1)"
    printf 'RCLONE_SHA=%s\n' "$(printf '%s\n' "$inst" | sed -n 's/^RCLONE_SHA=//p' | head -n 1)"
} > "$T/tmp/ursanas-rclone.env"
cp /ursanas/ursanas-deps.sh "$T/tmp/ursanas-deps.sh"
next

# --- 2 to 5. the dependencies, one stage at a time
step ursanas/progress-zfs;    in-target sh /tmp/ursanas-deps.sh zfs    || true; next
step ursanas/progress-docker; in-target sh /tmp/ursanas-deps.sh docker || true; next
step ursanas/progress-kvm;    in-target sh /tmp/ursanas-deps.sh kvm    || true; next
step ursanas/progress-rclone; in-target sh /tmp/ursanas-deps.sh rclone || true; next

# --- 6. the first-boot installer
step ursanas/progress-finish
rm -f "$T/tmp/ursanas-deps.sh" "$T/tmp/ursanas-rclone.env"

# NFS and FTP stay off until they are switched on from the Services page (install.sh does the same when it installs them).
in-target systemctl disable nfs-server vsftpd || true

cp /ursanas/ursanas-firstboot.sh "$T/usr/local/sbin/ursanas-firstboot"
chmod 755 "$T/usr/local/sbin/ursanas-firstboot"
cp /ursanas/ursanas-firstboot.service "$T/etc/systemd/system/ursanas-firstboot.service"
in-target systemctl enable ursanas-firstboot.service

# What the login screen shows until the installation is done.
mkdir -p "$T/etc/issue.d"
cat > "$T/etc/issue.d/50-ursanas.issue" <<'EOF'
UrsaNAS is being set up. This takes a minute or two.
This message changes when it is done.

EOF
next

db_progress STOP || true
