#!/bin/sh
# Run by the installer (partman/early_command) just before the partitioner, when the disks are known.
# Turns the system disk size chosen on the "UrsaNAS setup" page into a partitioning recipe: boot partition (UEFI),
# swap and root, then hands the recipe, and the disk when there is only one, to the partitioner.
. /usr/share/debconf/confmodule

ESP_MB=538          # same size as the installer's own EFI recipes
MIN_TOTAL_GB=16     # smallest accepted system disk size
MIN_ROOT_MB=10240   # smallest root partition (with the minimum total and up to 4 GB of swap)

# --- swap: as much as the RAM, between 1 and 4 GB
ram_mb=$(awk '/^MemTotal:/ { print int($2 / 1024) }' /proc/meminfo)
swap_mb=$ram_mb
[ "$swap_mb" -lt 1024 ] && swap_mb=1024
[ "$swap_mb" -gt 4096 ] && swap_mb=4096

# --- disks that can receive the system: all but the installation medium
parent_disk() {
    n=$(basename "$1")
    if [ -e "/sys/class/block/$n/partition" ]; then
        echo "/dev/$(basename "$(dirname "$(readlink -f "/sys/class/block/$n")")")"
    else
        echo "/dev/$n"
    fi
}
medium=$(grep ' /cdrom ' /proc/mounts | cut -d' ' -f1 | head -n 1 || true)
medium_disk=
[ -n "$medium" ] && medium_disk=$(parent_disk "$medium")

count=0
candidate=
smallest_mb=0
for d in $(list-devices disk); do
    [ "$d" = "$medium_disk" ] && continue
    count=$((count + 1))
    candidate=$d
    d_mb=$(( $(cat "/sys/class/block/$(basename "$d")/size") / 2048 ))
    if [ "$smallest_mb" -eq 0 ] || [ "$d_mb" -lt "$smallest_mb" ]; then smallest_mb=$d_mb; fi
done

# One disk: no need to ask which. Several: the installer's own disk question comes up.
if [ "$count" -eq 1 ]; then
    if [ "$smallest_mb" -lt $(( MIN_TOTAL_GB * 1024 )) ]; then
        db_input critical ursanas/disk-too-small || true
        db_go || true
        exit 1
    fi
    db_set partman-auto/disk "$candidate"
fi

# --- the size chosen on the "UrsaNAS setup" page
db_get ursanas/disk-size
size_gb=$RET
case $size_gb in '' | *[!0-9]*) size_gb=0 ;; esac

# The partitioner stretches the last partition over the whole disk whatever its maximum is, so a limited root
# partition gets an exact size (minimum = maximum). The whole disk is requested with -1: also when the asked
# size does not fit on the smallest disk.
if [ "$size_gb" -eq 0 ] || [ $(( size_gb * 1024 + 64 )) -ge "$smallest_mb" ]; then
    root_min=$MIN_ROOT_MB
    root_max=-1
else
    root_min=$(( size_gb * 1024 - ESP_MB - swap_mb ))
    [ "$root_min" -lt "$MIN_ROOT_MB" ] && root_min=$MIN_ROOT_MB
    root_max=$root_min
fi

# --- the recipe: the UEFI system partition, or the BIOS boot partition on a GPT disk
if [ -d /sys/firmware/efi ]; then
    boot_part="$ESP_MB $ESP_MB $ESP_MB free
	\$primary{ }
	\$iflabel{ gpt }
	\$reusemethod{ }
	method{ efi }
	format{ } ."
else
    boot_part="1 1 1 free
	\$iflabel{ gpt }
	\$bios_boot{ }
	method{ biosgrub } ."
fi

cat > /var/lib/ursanas-recipe <<EOF
ursanas ::

$boot_part

$swap_mb $swap_mb $swap_mb linux-swap
	\$primary{ }
	\$reusemethod{ }
	method{ swap }
	format{ } .

$root_min 1000 $root_max ext4
	\$primary{ }
	\$bootable{ }
	method{ format }
	format{ }
	use_filesystem{ }
	filesystem{ ext4 }
	mountpoint{ / } .
EOF

db_set partman-auto/expert_recipe_file /var/lib/ursanas-recipe

# The partitioner creates the last partition of a recipe with "full", which takes all the remaining space whatever
# the size asked. When the system is limited, create it with "beginning" instead, as a manual partitioning does:
# the root partition gets its exact size and the rest of the disk stays free.
if [ "$root_max" -ne -1 ]; then
    lib=/lib/partman/lib/auto-shared.sh
    sed -i 's/\$free_space full \${1}000001/$free_space beginning ${1}000001/' "$lib"
    if grep -q 'free_space full' "$lib"; then
        logger -t ursanas "partman-auto: the last partition could not be limited"
    fi
fi
exit 0
