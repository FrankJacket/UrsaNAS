# Installing UrsaNAS

This page is for whoever sets up the machine that will run UrsaNAS — it assumes you're comfortable installing an operating system and typing a few commands in a terminal. Once UrsaNAS itself is installed, nothing else in this wiki needs that: the rest is all done from the web interface. If this isn't something you're comfortable doing yourself, ask someone who is to do this part for you.

## 1. Prepare the machine

- Install a **Linux** server system, 64-bit (x86). UrsaNAS has been tested on **Debian 13+** and **Ubuntu Server 26.04**; other systems of the Debian family (which use the `apt` package manager) may work but are untested. Choose the **server** edition, without "Desktop" or any graphical interface — UrsaNAS is managed entirely from its web page on another device, so the machine itself never needs a screen, keyboard or desktop environment after this step. A minimal installation is enough; add the **OpenSSH server** when the installer offers it.
- Give it a fixed address (or a DHCP reservation) so you can always find it.
- You need a user with `sudo` rights and an internet connection (the installer downloads packages).
- Data disks: leave them empty and unmounted. You prepare them later from the web interface — see [Storage](Storage).
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

If the NAS cannot reach GitHub directly, download `ursanas-<version>-linux-x64.tar.gz` from the Releases page in your browser, copy it to the NAS (`scp ursanas-*.tar.gz <user>@<nas-address>:`), then:

```bash
tar xzf ursanas-*-linux-x64.tar.gz
cd ursanas-*-linux-x64
sudo ./install.sh
```

## Next

Once the installer finishes and prints an address, continue with **[Getting Started](Getting-Started)** to sign in and start setting things up.
