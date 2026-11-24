#!/bin/bash
# Builds the UrsaNAS installation ISO: the official Debian netinst ISO, plus an installer that asks only a few
# questions (see preseed.cfg), installs the UrsaNAS dependencies with Linux (see late.sh and ursanas-deps.sh) and
# installs the UrsaNAS package at the first start of the new system (see ursanas-firstboot.sh).
# Run it on Linux or in WSL (Debian or Ubuntu); it does not need root and it never modifies the Debian ISO it starts from.
#
#   deploy/iso/build-iso.sh --debian-iso debian-13.x.y-amd64-netinst.iso --package ursanas-X.Y.Z-linux-x64.tar.gz \
#                           [--output ursanas-X.Y.Z-installer.iso] [--sha256 <expected checksum of the Debian ISO>]
#
# Needs: xorriso cpio gzip rsvg-convert (librsvg2-bin) convert (imagemagick) fonts-dejavu-core
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../.." && pwd)

die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
say() { printf '\n==> %s\n' "$*"; }

DEBIAN_ISO= PACKAGE= OUTPUT= SHA256=
while [ $# -gt 0 ]; do
    case "$1" in
        --debian-iso) DEBIAN_ISO=$2; shift 2 ;;
        --package)    PACKAGE=$2; shift 2 ;;
        --output)     OUTPUT=$2; shift 2 ;;
        --sha256)     SHA256=$2; shift 2 ;;
        -h|--help)    sed -n "2,11p" "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *)            die "unknown option: $1" ;;
    esac
done
[ -f "$DEBIAN_ISO" ] || die "--debian-iso: file not found"
[ -f "$PACKAGE" ] || die "--package: file not found (the ursanas-X.Y.Z-linux-x64.tar.gz of a release)"
for tool in xorriso cpio gzip rsvg-convert convert sha256sum awk; do
    command -v "$tool" > /dev/null || die "missing tool: $tool"
done

VERSION=$(basename "$PACKAGE" | sed -E 's/^ursanas-(.+)-linux-x64\.tar\.gz$/\1/')
[ -n "$OUTPUT" ] || OUTPUT="$REPO/artifacts-iso/ursanas-$VERSION-installer.iso"
mkdir -p "$(dirname "$OUTPUT")"

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

say "Checking the Debian ISO"
ACTUAL=$(sha256sum "$DEBIAN_ISO" | cut -d' ' -f1)
echo "SHA-256: $ACTUAL"
if [ -n "$SHA256" ] && [ "$SHA256" != "$ACTUAL" ]; then die "the checksum does not match --sha256"; fi
xorriso -osirrox on -indev "$DEBIAN_ISO" -extract /.disk/info "$WORK/disk-info" > /dev/null 2>&1 \
    || die "this does not look like a Debian installation ISO"
echo "Debian: $(cat "$WORK/disk-info")"

say "Extracting what is modified"
mkdir -p "$WORK/orig"
xorriso -osirrox on -indev "$DEBIAN_ISO" \
    -extract /install.amd/gtk/initrd.gz "$WORK/orig/gtk-initrd.gz" \
    -extract /install.amd/initrd.gz "$WORK/orig/txt-initrd.gz" \
    -extract /md5sum.txt "$WORK/orig/md5sum.txt" > /dev/null 2>&1 \
    || die "cannot read the installer files of the Debian ISO"
chmod -R u+w "$WORK/orig"

say "Drawing the banner and the menu background"
rsvg-convert -w 48 -h 48 "$HERE/logo.svg" -o "$WORK/logo48.png"
rsvg-convert -w 80 -h 80 "$HERE/logo.svg" -o "$WORK/logo80.png"
mkdir -p "$WORK/art"
banner() { # banner BACKGROUND OUTPUT: the strip at the top of the graphical installer
    convert -size 800x75 "xc:$1" "$WORK/logo48.png" -geometry +20+13 -composite \
        -font DejaVu-Sans-Bold -pointsize 30 -fill white -annotate +82+50 'UrsaNAS' "$2"
}
banner '#5b4ae0' "$WORK/art/logo_debian.png"
banner '#2b2570' "$WORK/art/logo_debian_dark.png"
# The same drawing for both menus: the logo, the name and "Installer", without background.
# GRUB (UEFI) shows it as an image of its theme at its own size, never stretched; ISOLINUX (BIOS) on a 640x480 background.
convert -size 360x160 'xc:none' "$WORK/logo80.png" -gravity north -geometry +0+0 -composite \
    -font DejaVu-Sans-Bold -pointsize 34 -fill white -gravity north -annotate +0+88 'UrsaNAS' \
    -font DejaVu-Sans -pointsize 14 -fill '#a3a1c0' -annotate +0+134 'Installer' "$WORK/art/banner.png"
convert -size 640x480 'xc:#12111f' "$WORK/art/banner.png" -gravity north -geometry +0+24 -composite "$WORK/art/splash.png"

# --- what is added at the end of both installer initrds (a second cpio archive: the kernel unpacks them one after the other)
build_overlay() { # build_overlay NAME ORIGINAL-INITRD -> $WORK/NAME-initrd.gz
    local name=$1 orig=$2 dir="$WORK/overlay-$1"
    rm -rf "$dir"; mkdir -p "$dir/ursanas" "$dir/var/lib/dpkg/info" "$dir/usr/share/graphics"

    tr -d '\r' < "$HERE/preseed.cfg" > "$dir/preseed.cfg"
    for f in late.sh partman-early.sh ursanas-deps.sh ursanas-firstboot.sh ursanas-firstboot.service; do tr -d '\r' < "$HERE/$f" > "$dir/ursanas/$f"; done
    tr -d '\r' < "$HERE/ursanas-setup.postinst" > "$dir/var/lib/dpkg/info/ursanas-setup.postinst"
    tr -d '\r' < "$HERE/ursanas-setup.templates" > "$dir/var/lib/dpkg/info/ursanas-setup.templates"
    printf '#!/bin/sh\nexit 0\n' > "$dir/var/lib/dpkg/info/ursanas-setup.isinstallable"
    cp "$WORK/art/logo_debian.png" "$WORK/art/logo_debian_dark.png" "$dir/usr/share/graphics/"

    # The "UrsaNAS setup" page joins the installer's main menu just before the network setup (1800), after the
# installer components are loaded from the medium (1600): the questions it takes over exist by then.
    mkdir -p "$WORK/x-$name"
    gzip -dc "$orig" | (cd "$WORK/x-$name" && cpio -idm --quiet --no-absolute-filenames '*var/lib/dpkg/status')
    cp "$WORK/x-$name/var/lib/dpkg/status" "$dir/var/lib/dpkg/status"
    printf '\nPackage: ursanas-setup\nStatus: install ok unpacked\nVersion: 1\nDepends: cdebconf-udeb, di-utils\nDescription: UrsaNAS setup\nInstaller-Menu-Item: 1700\n\n' \
        >> "$dir/var/lib/dpkg/status"

    chmod 755 "$dir/ursanas/late.sh" "$dir/ursanas/partman-early.sh" "$dir/ursanas/ursanas-deps.sh" "$dir/ursanas/ursanas-firstboot.sh" \
        "$dir/var/lib/dpkg/info/ursanas-setup.postinst" "$dir/var/lib/dpkg/info/ursanas-setup.isinstallable"
    (cd "$dir" && find . -mindepth 1 | LC_ALL=C sort | cpio -o -H newc -R 0:0 --quiet | gzip -9n) > "$WORK/$name-overlay.cpio.gz"
    cat "$orig" "$WORK/$name-overlay.cpio.gz" > "$WORK/$name-initrd.gz"
}
say "Preparing the installer (graphical and text)"
build_overlay gtk "$WORK/orig/gtk-initrd.gz"
build_overlay txt "$WORK/orig/txt-initrd.gz"

say "Writing the boot menus"
mkdir -p "$WORK/boot"
# UEFI menu (GRUB)
cat > "$WORK/boot/grub.cfg" <<'EOF'
if [ x$feature_default_font_path = xy ] ; then
   font=unicode
else
   font=$prefix/font.pf2
fi

if loadfont $font ; then
  set gfxmode=800x600
  set gfxpayload=keep
  insmod efi_gop
  insmod efi_uga
  insmod video_bochs
  insmod video_cirrus
  insmod gfxterm
  insmod png
  terminal_output gfxterm
fi

set color_normal=light-gray/black
set color_highlight=white/black

set theme=/boot/grub/theme/ursanas
set default=0

menuentry --hotkey=d 'Install UrsaNAS: network by DHCP' {
    set background_color=black
    linux    /install.amd/vmlinuz vga=788 --- quiet
    initrd   /install.amd/gtk/initrd.gz
}
menuentry --hotkey=s 'Install UrsaNAS: static IP address' {
    set background_color=black
    linux    /install.amd/vmlinuz vga=788 netcfg/disable_autoconfig=true --- quiet
    initrd   /install.amd/gtk/initrd.gz
}
submenu --hotkey=t 'Text mode (if the graphical installer does not start) ...' {
    set theme=/boot/grub/theme/ursanas
    menuentry 'Install UrsaNAS in text mode: network by DHCP' {
        set background_color=black
        linux    /install.amd/vmlinuz vga=788 --- quiet
        initrd   /install.amd/initrd.gz
    }
    menuentry 'Install UrsaNAS in text mode: static IP address' {
        set background_color=black
        linux    /install.amd/vmlinuz vga=788 netcfg/disable_autoconfig=true --- quiet
        initrd   /install.amd/initrd.gz
    }
}
EOF
cat > "$WORK/boot/theme" <<'EOF'
title-text: ""
desktop-color: "#12111f"
message-color: "white"
message-bg-color: "#12111f"
terminal-font: "Sans Regular 12"

# The banner (logo and name) keeps its size on any screen; the menu starts below it, in pixels too.
+ image {
  left = 50%-180
  top = 24
  file = "banner.png"
}

+ boot_menu {
  left = 18%
  width = 64%
  top = 210
  height = 220
  item_font = "Sans Regular 12"
  item_color = #c0c0c0
  selected_item_color = "black"
  selected_item_pixmap_style = "hl_*.png"
  item_height = 24
  item_padding = 15
  item_spacing = 10
}
EOF
# BIOS menu (ISOLINUX)
cat > "$WORK/boot/isolinux.cfg" <<'EOF'
path
prompt 0
timeout 0
default vesamenu.c32
menu title
menu background splash.png
menu color title	* #FFFFFFFF *
menu color border	* #00000000 #00000000 none
menu color sel		* #ffffffff #76a1d0ff *
menu color hotsel	1;7;37;40 #ffffffff #76a1d0ff *
menu color tabmsg	* #ffffffff #00000000 *
menu vshift 12
menu rows 8
menu tabmsgrow 18
menu tabmsg Press ENTER to boot or TAB to edit a menu entry

label dhcp
	menu label ^Install UrsaNAS: network by DHCP
	menu default
	kernel /install.amd/vmlinuz
	append vga=788 initrd=/install.amd/gtk/initrd.gz --- quiet
label static
	menu label Install UrsaNAS: ^static IP address
	kernel /install.amd/vmlinuz
	append vga=788 netcfg/disable_autoconfig=true initrd=/install.amd/gtk/initrd.gz --- quiet
menu begin text
	menu label ^Text mode (if the graphical installer does not start)
	menu title Text mode
	label back
		menu label ^Back..
		menu exit
	label textdhcp
		menu label Install UrsaNAS in text mode: network by ^DHCP
		kernel /install.amd/vmlinuz
		append vga=788 initrd=/install.amd/initrd.gz --- quiet
	label textstatic
		menu label Install UrsaNAS in text mode: ^static IP address
		kernel /install.amd/vmlinuz
		append vga=788 netcfg/disable_autoconfig=true initrd=/install.amd/initrd.gz --- quiet
menu end
EOF

say "Embedding the UrsaNAS package ($VERSION)"
mkdir -p "$WORK/pkg"
cp "$PACKAGE" "$WORK/pkg/ursanas.tar.gz"
(cd "$WORK/pkg" && sha256sum ursanas.tar.gz > ursanas.tar.gz.sha256)

say "Fixing the checksum list of the ISO"
REPLACED='\./(install\.amd/(gtk/)?initrd\.gz|isolinux/isolinux\.cfg|isolinux/splash\.png|boot/grub/grub\.cfg|boot/grub/theme/ursanas|boot/grub/theme/banner\.png|ursanas/.*)'
grep -v -E "  $REPLACED\$" "$WORK/orig/md5sum.txt" > "$WORK/md5sum.new"
add_md5() { printf '%s  %s\n' "$(md5sum "$1" | cut -d' ' -f1)" "$2" >> "$WORK/md5sum.new"; }
add_md5 "$WORK/gtk-initrd.gz" ./install.amd/gtk/initrd.gz
add_md5 "$WORK/txt-initrd.gz" ./install.amd/initrd.gz
add_md5 "$WORK/boot/isolinux.cfg" ./isolinux/isolinux.cfg
add_md5 "$WORK/art/splash.png" ./isolinux/splash.png
add_md5 "$WORK/boot/grub.cfg" ./boot/grub/grub.cfg
add_md5 "$WORK/boot/theme" ./boot/grub/theme/ursanas
add_md5 "$WORK/art/banner.png" ./boot/grub/theme/banner.png
add_md5 "$WORK/pkg/ursanas.tar.gz" ./ursanas/ursanas.tar.gz
add_md5 "$WORK/pkg/ursanas.tar.gz.sha256" ./ursanas/ursanas.tar.gz.sha256

say "Writing the ISO"
rm -f "$OUTPUT"
xorriso -indev "$DEBIAN_ISO" -outdev "$OUTPUT" -overwrite on -boot_image any replay \
    -map "$WORK/gtk-initrd.gz" /install.amd/gtk/initrd.gz \
    -map "$WORK/txt-initrd.gz" /install.amd/initrd.gz \
    -map "$WORK/boot/isolinux.cfg" /isolinux/isolinux.cfg \
    -map "$WORK/art/splash.png" /isolinux/splash.png \
    -map "$WORK/boot/grub.cfg" /boot/grub/grub.cfg \
    -map "$WORK/boot/theme" /boot/grub/theme/ursanas \
    -map "$WORK/art/banner.png" /boot/grub/theme/banner.png \
    -map "$WORK/pkg" /ursanas \
    -map "$WORK/md5sum.new" /md5sum.txt \
    -commit > "$WORK/xorriso.log" 2>&1 || { cat "$WORK/xorriso.log"; die "xorriso failed"; }

say "Done"
ls -l "$OUTPUT"
sha256sum "$OUTPUT"
