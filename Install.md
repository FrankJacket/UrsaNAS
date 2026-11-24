# Installing UrsaNAS

UrsaNAS runs on a dedicated machine (physical or virtual) with a fresh **Linux** installation, 64-bit x86. It has been tested on **Debian 13+** and **Ubuntu Server 26.04**. The installer relies on the `apt` package manager, so other distributions of the Debian family may work but are untested; it warns you if it does not recognise the system. The installer does everything else: it installs the packages UrsaNAS needs, copies the program, and starts it.

You can also install Linux and UrsaNAS together, in one go, from an ISO that you build yourself: see [Installing from the UrsaNAS ISO](#installing-from-the-ursanas-iso).

## 1. Prepare the machine

- Install a Linux server system: Debian 13+ or Ubuntu Server 26.04 (the tested ones). A minimal installation is enough; add the **OpenSSH server** when the installer offers it.
- Give it a fixed address (or a DHCP reservation) so you can always find it.
- You need a user with `sudo` rights and an internet connection (the installer downloads packages).
- Data disks: leave them empty and unmounted. You prepare them later from the web interface.
- Recommended: 2 CPU cores and **4 GB of memory** (2 GB is the minimum; more if you use ZFS, containers or virtual machines). On a virtual machine, give it a **fixed** amount of memory: with Hyper-V, switch off *Dynamic Memory*, otherwise the system may kill the installation for lack of memory.

## 2. Install

On the NAS, run the installer. It downloads the newest release, checks it, installs it and starts it:

```bash
curl -fsSL https://raw.githubusercontent.com/FrankJacket/UrsaNAS/master/deploy/install.sh | sudo sh
```

With `wget` instead of `curl`:

```bash
wget -qO- https://raw.githubusercontent.com/FrankJacket/UrsaNAS/master/deploy/install.sh | sudo sh
```

That is all. It takes a few minutes (longer on Debian, where the ZFS module is compiled). At the end it prints the address of the interface.

### Option

Docker (with Docker Compose) and virtual machine support (KVM, libvirt) are always installed. There is one option, added after `sh -s --`:

| Option | Effect |
|---|---|
| `--no-zfs` | Does not install ZFS (no pools or datasets). Choose this if you only use ext4, Btrfs or exFAT disks. |

For example:

```bash
curl -fsSL https://raw.githubusercontent.com/FrankJacket/UrsaNAS/master/deploy/install.sh | sudo sh -s -- --no-zfs
```

### Installing from a downloaded file

If the NAS cannot reach GitHub directly, download `ursanas-<version>-linux-x64.tar.gz` from the [Releases page](https://github.com/FrankJacket/UrsaNAS/releases) in your browser, copy it to the NAS (`scp ursanas-*.tar.gz <user>@<nas-address>:`), then:

```bash
tar xzf ursanas-*-linux-x64.tar.gz
cd ursanas-*-linux-x64
sudo ./install.sh
```

### What the installer does

- Installs Docker with Compose, KVM and libvirt (virtual machines), Samba, NFS, FTP (vsftpd), SMART monitoring, file system tools (Btrfs, exFAT, XFS), SMB/NFS client tools for remote storage, FUSE, and ZFS. NFS and FTP stay stopped until you switch them on from the *Services* page.
- Debian only: it enables the `contrib` repository component, which provides ZFS (and builds the ZFS module, which takes longer). Ubuntu already includes ZFS.
- Installs the official `rclone` (checksum verified) for OneDrive and Dropbox.
- Creates the `ursanas` system account, copies the program to `/opt/ursanas`, and starts two services: `ursanas-agent` (the privileged part, reachable only through a local socket) and `ursanas-web` (the interface, on port 8080).
- Does **not** touch your disks, your network settings or your firewall.

## 3. First start

1. Open `http://<nas-address>:8080` in a browser.
2. On the first visit a form asks you to **create the first administrator**. Choose a strong password: this account manages the whole NAS.
3. Go to *Disks* to prepare your data disks, then *Shares* to share folders.

The interface speaks plain HTTP. Use it on your home network only, and do not expose port 8080 to the internet. If a firewall is enabled (for example `ufw`), allow the ports you use: 8080 (interface), 445 (SMB), 2049 (NFS), 21 (FTP).

If you installed ZFS and the installer told you the module is not loaded, restart the machine once (`sudo reboot`).

## Installing from the UrsaNAS ISO

Instead of installing Linux and then running the installer, you can install both in one go. You build an installation ISO from the official Debian installation image; the machine then gets Debian 13, an SSH server, `sudo`, everything UrsaNAS needs, and UrsaNAS itself. The installer asks only what a NAS needs: language, location and keyboard, then one page for the machine name, the first user and the size of the system disk.

### What you need

- A machine, physical or virtual, **that you can erase: the installation erases the system disk** (several disks: the installer asks which one). UEFI is recommended. At least 16 GB of disk for the system, 2 CPU cores and 4 GB of memory.
- Internet access during the installation: the installer downloads the packages (Debian mirror `deb.debian.org`) and `rclone`.
- On Hyper-V: a **generation 2** machine with **Secure Boot switched off** (or with the *Microsoft UEFI Certificate Authority* template), and a fixed amount of memory (no *Dynamic Memory*).

### Building the ISO

On a Linux machine or in WSL (Debian or Ubuntu). Building does not need root; installing the tools does.

1. Install the tools: `sudo apt install xorriso cpio gzip librsvg2-bin imagemagick fonts-dejavu-core`
2. Download the **Debian 13 installation image** (`debian-13.x.y-amd64-netinst.iso`) from [debian.org](https://www.debian.org/distrib/netinst) and, next to it, the `SHA256SUMS` file to check it.
3. Download `ursanas-<version>-linux-x64.tar.gz` from the [Releases page](https://github.com/FrankJacket/UrsaNAS/releases).
4. Build:

```bash
deploy/iso/build-iso.sh --debian-iso debian-13.8.0-amd64-netinst.iso \
                        --package ursanas-<version>-linux-x64.tar.gz \
                        --sha256 <checksum of the Debian image, from SHA256SUMS>
```

The ISO is written to `artifacts-iso/ursanas-<version>-installer.iso` (use `--output` to choose another place). The script checks the checksum when you give `--sha256`, and it never modifies the Debian image.

### Installing

1. Start the machine from the ISO (write it to a USB drive, or attach it to the virtual machine). The boot menu offers *network by DHCP* (the default), *static IP address*, and, in a submenu, a text mode in case the graphical installer does not start.
2. Answer the standard Debian screens: language, country, keyboard, and time zone when the country has several.
3. On the **UrsaNAS setup** page, enter the machine name (`ursanas` by default), your name, a username and a password (twice), and the **size of the system disk in GB**: 64 by default, at least 16, or 0 to use the whole disk. With *static IP address*, the installer then asks for the network settings.
4. The disk is partitioned for you: an EFI system partition (UEFI), a swap partition (as large as the memory, between 1 and 4 GB), and a root file system. They fill the size you asked for; **the rest of the disk is left untouched and free**. If the disk already holds data, the installer asks you to confirm before it writes.
5. Wait. A progress bar shows the steps. Installing ZFS can take several minutes: it is compiled for your kernel.
6. The machine restarts. At the first start, UrsaNAS itself is installed (a minute or two); the login screen shows the step in progress, then *UrsaNAS is ready* with the address to open. Continue with [First start](#3-first-start).

The user you created has `sudo` rights; the root account is disabled. The SSH server is running.

### Good to know

- The installation does **not** update the system: Debian's own packages are those of the image and its mirror at that moment. Run `sudo apt update && sudo apt upgrade` when you want the latest updates.
- NFS and FTP are installed but stopped, as with the normal installer; switch them on from the *Services* page.
- If something could not be installed during the installation (a package, or `rclone`), the installer carries on and the first start installs what is missing, as the normal installer does.
- Tested so far: UEFI virtual machine on Hyper-V (generation 2, Secure Boot off), network by DHCP, one disk. **Not tested yet:** BIOS boot, Secure Boot, text mode, the static IP entry, several disks, a disk that already holds data.

### If something goes wrong

| Symptom | What to do |
|---|---|
| *Installation step failed* during the installation | Note the step it names. Press `Ctrl+Alt+F4` to read the installer log (the line starting with `E:` names the cause). |
| ZFS, Docker or KVM missing after the installation | Read `/var/log/ursanas-deps.log` (every step carries its time), then run the normal installer (step 2 above), which installs what is missing. |
| The login screen does not say *UrsaNAS is ready* | `sudo systemctl status ursanas-firstboot`, read `/var/log/ursanas-firstboot.log`, then `sudo systemctl restart ursanas-firstboot`. |

## Updating

Run the installer command of step 2 again (or install from a newer downloaded file). Your accounts, shares, settings and backups jobs are kept.

## Checking and troubleshooting

```bash
systemctl status ursanas-agent ursanas-web     # both must be "active (running)"
journalctl -u ursanas-agent -n 50              # recent log of the agent
journalctl -u ursanas-web -n 50                # recent log of the interface
cat /opt/ursanas/VERSION                       # installed version
```

| Symptom | What to do |
|---|---|
| The page does not load | Check the address, and that the machine's firewall allows port 8080. |
| "Agent unreachable" in the interface | `sudo systemctl restart ursanas-agent`, then read its log. |
| ZFS is missing on Debian | Make sure the `contrib` component is enabled, run `sudo apt-get install zfs-dkms zfsutils-linux`, then reboot. |
| A feature is greyed out | The program behind it is not installed or not running. Re-run the installer, then check the message on the feature's page. |

## Uninstalling

```bash
sudo systemctl disable --now ursanas-web ursanas-agent
sudo rm -rf /opt/ursanas /etc/systemd/system/ursanas-*.service
sudo systemctl daemon-reload
```

Data is kept in `/var/lib/ursanas` and `/var/lib/ursanas-web` (accounts, settings, database). Delete them too for a complete removal. Your shared files are never touched.
