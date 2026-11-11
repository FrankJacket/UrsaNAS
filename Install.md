# Installing UrsaNAS

UrsaNAS runs on a dedicated machine (physical or virtual) with a fresh **Debian 13** or **Ubuntu Server 26.04** installation, 64-bit x86. The installer does everything else: it installs the packages UrsaNAS needs, copies the program, and starts it.

## 1. Prepare the machine

- Install Debian 13 or Ubuntu Server 26.04. A minimal installation is enough; add the **OpenSSH server** when the installer offers it.
- Give it a fixed address (or a DHCP reservation) so you can always find it.
- You need a user with `sudo` rights and an internet connection (the installer downloads packages).
- Data disks: leave them empty and unmounted. You prepare them later from the web interface.
- Recommended: 2 CPU cores and 4 GB of memory (more if you use ZFS, containers or virtual machines).

## 2. Install

UrsaNAS is hosted in a private GitHub repository, so the installer needs a read-only access token. Create one once, on <https://github.com/settings/personal-access-tokens/new>:

- *Repository access*: **Only select repositories** → `FrankJacket/UrsaNAS`.
- *Permissions* → *Repository permissions* → **Contents: Read-only**.

Then, on the NAS, paste your token and run the installer. It downloads the newest release, checks it, installs it and starts it:

```bash
export GITHUB_TOKEN=<your-token>
curl -fsSL -H "Authorization: Bearer $GITHUB_TOKEN" https://raw.githubusercontent.com/FrankJacket/UrsaNAS/master/deploy/install.sh | sudo GITHUB_TOKEN="$GITHUB_TOKEN" sh
```

With `wget` instead of `curl`:

```bash
export GITHUB_TOKEN=<your-token>
wget -qO- --header="Authorization: Bearer $GITHUB_TOKEN" https://raw.githubusercontent.com/FrankJacket/UrsaNAS/master/deploy/install.sh | sudo GITHUB_TOKEN="$GITHUB_TOKEN" sh
```

That is all. It takes a few minutes (longer on Debian, where the ZFS module is compiled). At the end it prints the address of the interface. If the repository is ever made public, the token and the `-H` / `--header` option are no longer needed.

### Options

Add options after `sh -s --`. For example, to also install Docker and virtual machine support:

```bash
curl -fsSL -H "Authorization: Bearer $GITHUB_TOKEN" https://raw.githubusercontent.com/FrankJacket/UrsaNAS/master/deploy/install.sh | sudo GITHUB_TOKEN="$GITHUB_TOKEN" sh -s -- --with-docker --with-vm
```

| Option | Effect |
|---|---|
| `--with-docker` | Also installs Docker and Docker Compose, which enables the *Docker* menu. |
| `--with-vm` | Also installs KVM and libvirt, which enables the *Virtual machines* page. |
| `--no-zfs` | Does not install ZFS (no pools or datasets). Choose this if you only use ext4, Btrfs or exFAT disks. |

You can run the installer again later to add an option.

### Installing from a downloaded file

Without a token on the NAS, download `ursanas-<version>-linux-x64.tar.gz` from the [Releases page](https://github.com/FrankJacket/UrsaNAS/releases) in your browser, copy it to the NAS (`scp ursanas-*.tar.gz <user>@<nas-address>:`), then:

```bash
tar xzf ursanas-*-linux-x64.tar.gz
cd ursanas-*-linux-x64
sudo ./install.sh
```

### What the installer does

- Installs Samba, NFS, FTP (vsftpd), SMART monitoring, file system tools (Btrfs, exFAT, XFS), SMB/NFS client tools for remote storage, FUSE, and ZFS. NFS and FTP stay stopped until you switch them on from the *Services* page.
- On Debian, it enables the `contrib` repository component, which provides ZFS.
- Installs the official `rclone` (checksum verified) for OneDrive and Dropbox.
- Creates the `ursanas` system account, copies the program to `/opt/ursanas`, and starts two services: `ursanas-agent` (the privileged part, reachable only through a local socket) and `ursanas-web` (the interface, on port 8080).
- Does **not** touch your disks, your network settings or your firewall.

## 3. First start

1. Open `http://<nas-address>:8080` in a browser.
2. On the first visit a form asks you to **create the first administrator**. Choose a strong password: this account manages the whole NAS.
3. Go to *Disks* to prepare your data disks, then *Shares* to share folders.

The interface speaks plain HTTP. Use it on your home network only, and do not expose port 8080 to the internet. If a firewall is enabled (for example `ufw`), allow the ports you use: 8080 (interface), 445 (SMB), 2049 (NFS), 21 (FTP).

If you installed ZFS and the installer told you the module is not loaded, restart the machine once (`sudo reboot`).

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
| A feature is greyed out | The program behind it is not installed. Re-run the installer, with `--with-docker` or `--with-vm` if needed. |

## Uninstalling

```bash
sudo systemctl disable --now ursanas-web ursanas-agent
sudo rm -rf /opt/ursanas /etc/systemd/system/ursanas-*.service
sudo systemctl daemon-reload
```

Data is kept in `/var/lib/ursanas` and `/var/lib/ursanas-web` (accounts, settings, database). Delete them too for a complete removal. Your shared files are never touched.
