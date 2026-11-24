#!/bin/bash
# Installs UrsaNAS, once, at the first start of a machine installed from the UrsaNAS ISO. Run by ursanas-firstboot.service.
# The package was copied to /opt/ursanas-setup by the installer; the normal UrsaNAS installer does the rest.
# On failure the service stays enabled and tries again at the next start.
set -u -o pipefail

SETUP=/opt/ursanas-setup
LOG=/var/log/ursanas-firstboot.log
ISSUE=/etc/issue.d/50-ursanas.issue

say() { printf '\n[UrsaNAS %s] %s\n' "$(date +%H:%M:%S)" "$*" | tee -a "$LOG"; }

set_issue() {
    printf '%s\n\n' "$1" > "$ISSUE"
    agetty --reload 2>/dev/null || true
}

# The login screen shows the step in progress; the details go to the log and the journal, not to the screen.
step() {
    set_issue "UrsaNAS is being set up. This takes a minute or two.
Step in progress: $1"
}

fail() {
    say "Installation failed: $*"
    set_issue "UrsaNAS installation failed: $*
See $LOG, fix the cause, then run: sudo systemctl restart ursanas-firstboot"
    exit 1
}

# The dependencies (Docker, Samba, ZFS and the rest) were installed with Linux; the network only helps if one is missing.
say "Waiting for the network"
step "waiting for the network"
online=0
for _ in $(seq 1 60); do
    if getent hosts deb.debian.org > /dev/null 2>&1; then online=1; break; fi
    sleep 2
done
[ "$online" -eq 1 ] || say "No internet access: continuing, the dependencies were installed with Linux"

say "Checking the UrsaNAS package"
step "checking the package"
(cd "$SETUP" && sha256sum -c ursanas.tar.gz.sha256) 2>&1 | tee -a "$LOG"
[ "${PIPESTATUS[0]}" -eq 0 ] || fail "the UrsaNAS package is damaged"

say "Installing UrsaNAS: this takes a minute or two"
tar -xzf "$SETUP/ursanas.tar.gz" -C "$SETUP" || fail "cannot unpack the UrsaNAS package"
dir=$(ls -d "$SETUP"/ursanas-*/ 2> /dev/null | head -n 1)
[ -n "$dir" ] && [ -f "${dir}install.sh" ] || fail "unexpected package layout"

# install.sh announces each of its steps with a line starting with "==> ": they become the step shown on the screen.
step "installing UrsaNAS"
(cd "$dir" && sh ./install.sh) 2>&1 | while IFS= read -r line; do
    printf '%s\n' "$line" >> "$LOG"
    printf '%s\n' "$line"
    case $line in '==> '*) step "${line#==> }" ;; esac
done
[ "${PIPESTATUS[0]}" -eq 0 ] || fail "the UrsaNAS installer reported an error"

say "UrsaNAS is installed"
rm -rf "$SETUP"
systemctl disable ursanas-firstboot.service > /dev/null 2>&1 || true
set_issue "UrsaNAS is ready.
Open http://\\4:8080 in a browser and create the first administrator."
