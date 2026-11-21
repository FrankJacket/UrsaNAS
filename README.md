# UrsaNAS

UrsaNAS is a home NAS: a web interface (English / French) to manage disks, ZFS storage, file sharing, accounts, monitoring and backups to a USB disk on a Linux machine (tested on Debian 13+ and Ubuntu Server 26.04).

**To install it, see [Install.md](Install.md).** To use it, see the **[user wiki](https://github.com/FrankJacket/UrsaNAS/wiki)**.

It is written in C# (.NET 10) with MudBlazor, SQLite and Dapper.

## What you can do with it

The menu is organised in groups. The Docker group only appears when Docker is installed and running.

| Group | Pages | Purpose |
|---|---|---|
| **Storage** | Disks, Pools, Datasets, Backups | Partition, format, mount and erase disks, extend the system disk, create ZFS pools and datasets, take snapshots, back up to a USB disk |
| **Sharing** | Shares, Folders, Services | Share folders over SMB, NFS and FTP, set per-folder permissions (ACLs), start or stop services |
| **Network** | Network | Overview of the network interfaces (status, live throughput curve, speed, MTU, IPv4/IPv6, gateway, DNS, MAC) and editing of their IPv4/IPv6 addressing |
| **Docker** | Containers, Images, Stacks, Volumes and networks | Run and monitor containers, manage images, Compose stacks, volumes and networks |
| **Virtual machines** | Virtual machines | Create, run and control KVM machines, with a browser console |
| **Accounts** | Users, Groups, Admins | NAS accounts, groups, web interface administrators |
| **Monitoring** | Dashboard, Alerts, Audit log | Health overview, alerts (with email), log of actions |
| **Top bar** | Terminal icon | A root shell on the NAS in its own browser window |

### Sharing

- **SMB (Samba)**: fine-grained per-folder permissions (ACLs, users and groups).
- **File Manager**: browse, create, rename, move, copy and delete folders and files on all shares (the permissions editor is only for shares with advanced permissions), and also on **mounted disks** (USB…) under `/mnt` or `/media`. Disks and plain shares have no permission editor (plain shares use basic Unix rights, disks have none). Copy and move work between shares and disks; the item takes the permissions of its destination, and a share's own rules are not carried to another location.
- **NFS**: per share, with a list of allowed clients.
- **FTP (vsftpd)**: uses the NAS accounts, each user locked into their own folder. The service is enabled from the *Services* page.

ACLs only apply to SMB. NFS and FTP give access to the whole share.

### Backups

The **Backups** page copies shares, or only the folders and files you tick in a tree spanning all shares, to a **USB disk plugged into the NAS**. The copy is done by the [Ursacopy](https://github.com/FrankJacket/Ursacopy) engine, included as a Git submodule.

- **It is a readable copy, not a restorable image.** Files are copied as they are, without permissions or ACLs. The disk can be read on any computer (NTFS and exFAT work well).
- **One job = any selection** of shares, folders and files, copied into a folder on the disk (one sub-folder per share). Older single-share jobs keep their layout until a second share is added. Several jobs can share the same disk.
- **Scheduling**: manual, every day at a given time, or whenever the disk is connected.
- **ZFS snapshot option**: for a share on a ZFS dataset, the copy reads from a temporary snapshot, so files that change during the copy (databases, virtual disks) are copied consistently. The snapshot is deleted afterwards.
- **Trash**: deleted or replaced files are kept for N days (30 by default) on the disk.
- **Verification**: optionally re-read every copied file.
- **Alerts**: a failed copy, or a scheduled job with no successful copy for 3 days, raises an alert.

**First use**

1. Plug the disk and **mount it by hand** under `/mnt` or `/media`. The interface does not mount disks.
2. In *Backups*, create a job and pick the disk.
3. Click **Initialise the disk**. A small marker file (`.ursacopy-target`) is written at its root. It prevents writing to a different disk mounted at the same place.
4. Click **Run now**.

Safety: the agent only accepts a mount point under `/mnt`, `/media` or `/run/media` as a target. The web interface never sends a source path: the agent resolves the share folder itself.

### Docker

The installer (`Install.md`) installs Docker. UrsaNAS checks every few seconds whether Docker is installed and its service answers, and shows the **Docker** menu group only when it does. Until then, the `/docker` page (linked from *Services*) explains how to install or start it:

```bash
sudo apt install docker.io docker-compose
```

- **Containers**: list with state, ports, CPU and memory; start, stop, restart, delete; logs; run a simple container (image, ports, environment variables, volumes, network, restart policy) and edit it later in the same dialog. A restart-policy change is applied in place; any other change recreates the container, and the previous one is restored if the new one cannot start. Containers managed by a Compose stack are edited through their stack.
- **Images**: list, download (pull), delete, clean unused images.
- **Stacks**: Docker Compose files kept on the NAS (`/var/lib/ursanas/stacks/<name>/compose.yml`). Edit them in the browser, then start, stop, update (pull + up) or delete the stack. A file is checked with `docker compose config` before it is saved, so an invalid file never replaces a working one.
- **Volumes and networks**: list, create, delete.
- **NAS folders as volumes**: a button inserts the real path of a share folder (picked in the file tree) into the container dialog or the compose file.

Docker bypasses the share permissions (ACLs): a container that mounts a NAS folder reads and writes it with full rights. The interface says so where it matters. Names, images, ports, variables and volumes are validated before they reach the `docker` command, which is always run without a shell and never receives a value that could be read as an option.

### Disks

The **Disks** page lists each disk with its partitions. A disk is *free* when it holds no system, no mounted partition, no swap in use and no active ZFS pool; destructive actions are only offered (and enforced by the agent) on free disks.

- **Partition and format**: wipes the disk, creates one GPT partition using all the space and formats it (ext4, Btrfs, exFAT, or ZFS, which creates a single-device pool named after the label; each only when `mkfs.btrfs`, `mkfs.exfat` or `zpool` is installed). **Erase** removes every signature. Both ask you to type the disk name.
- **Format** a single partition, **Mount** it under `/mnt/<name>` (optionally at every start, through a UUID-based `/etc/fstab` line tagged `# ursanas` with `nofail`) and **Unmount** it. The first change to `/etc/fstab` keeps a copy in `/etc/fstab.ursanas-bak`.
- **Extend the system partition** after enlarging the system disk (for example a virtual disk): the root partition (ext4 or XFS) grows to the free space and its file system is resized online. If a swap partition follows it, that partition is removed and replaced by a `/swapfile` of the same size.

### Remote storage

*Storage → Remote storage* connects the NAS to a file service elsewhere: **SMB** (Windows, Samba, other NAS; `cifs-utils`), **NFS** (`nfs-common`), **Microsoft OneDrive** or **Dropbox** (`rclone` and `fuse3`). Each connection is mounted under `/srv/remote/<name>`, now and at every start. A type is only offered when its programs are installed.

- **Test the connection** checks it without saving anything: SMB and NFS are mounted in a temporary folder and unmounted, an online account is listed with `rclone lsd`.
- **SMB and NFS**: a connection is one `/etc/fstab` line tagged `# ursanas-remote` (with `_netdev,nofail`, so an unreachable server never blocks the start). There is no other database: the line is the list. The SMB password is only in a root-only file, `/var/lib/ursanas/remote/<name>.cred`, referenced by `credentials=`; it is never in `fstab`, in a command line or in the audit log. An SMB user left empty means guest access.
- **OneDrive and Dropbox**: the NAS has no browser to sign in with, so you sign in on a computer where [rclone](https://rclone.org) is installed, run `rclone authorize "onedrive"` (or `"dropbox"`), and paste the whole result (the token) into the dialog. UrsaNAS uses rclone's own application identifiers, so no account of yours with Microsoft or Dropbox is needed. The token goes to `/var/lib/ursanas/remote/<name>.rclone.conf` (root only, never in a command line, a log or an error message); rclone renews it by itself. Each connection is a systemd service, `ursanas-rclone-<name>.service` (`rclone mount`, restarted after a failure and at every start); `<name>.json` next to the token keeps the settings. An optional folder limits the connection to a sub-folder of the account.
- **rclone version**: `deploy/install.sh` installs the official rclone release it pins (checksum verified), not the Ubuntu package: rclone 1.60 sends its own built-in secret together with the application ID of the user, so it cannot renew the tokens of the connections made with the sign-in below.
- **OneDrive sign-in from the interface**: if `Remote:OneDriveClientId` (agent `appsettings.json`) holds the application ID of an Entra ID app registration (public client, "Allow public client flows" = yes, delegated permissions `Files.ReadWrite.All` and `offline_access`, any account type), the dialog shows a *Sign in to Microsoft* button: it displays a code to enter on microsoft.com/devicelogin (device code flow, no redirect, no secret), and the token is captured by the NAS and renewed by rclone with that same application. Without it, or on request, paste the result of `rclone authorize` as before. **Dropbox** works the same way with `Remote:DropboxAppKey` (a Dropbox app with "Full Dropbox" access, permissions `account_info.read`, `files.metadata.read/write`, `files.content.read/write`, "Allow public clients (Implicit Grant & PKCE)" on): the dialog opens Dropbox, which shows a code to paste back (OAuth 2 with PKCE, no secret).
- **Cache and speed**: an online connection has a cache on the NAS (`/var/cache/ursanas-rclone/<name>`): *full* (default, 10 GB; reads and writes go through it, the best compatibility), *writes only*, or none (slow, every access goes to the service). A speed limit in MB/s can protect the internet link. A cloud service is far slower than a local disk, and re-sharing a cloud mount over SMB works but is slow: for copies of your files, prefer a backup.
- **File Manager**: the *Remote* source lists the mounted connections; browse, create, rename, delete, and copy or move between a share, a disk and a remote storage (a copy to or from a cloud service goes through the cache and is as slow as the link). Remote storages are accepted by the File Manager and copies only, not as backup or virtual-machine storage targets.
- **Backups, containers and alerts**: a mounted remote storage can be the destination of a backup (it carries the same marker file as a disk; slower, so prefer it for off-site copies) and can be picked as a folder for a container (*Storage location* in the container form). Virtual-machine storage still refuses it. A saved connection that is not mounted raises a *Remote storage … not connected* alert (and an email, if enabled), closed when it is mounted again. A container started at boot can find the folder still empty if the remote storage is not mounted yet: restart the container once the connection is up.
- **Container capabilities**: the container form has a *Capabilities* section (Docker's "Runtime privilege and Linux capabilities"): a *Privileged* switch (`--privileged`) and one checkbox per Linux capability, the ones Docker grants by default being pre-checked and marked. Only the difference with the defaults is sent (`--cap-add` / `--cap-drop`); editing a container reads its current capabilities back, and saving a form whose effective rights did not change does not recreate the container.
- **Mount**, **Unmount** and **Remove** act on a saved connection. Removing it forgets the connection; the files on the remote server are never touched. **Edit** changes a connection's settings (server, path, user, folder, cache, speed limit, read-only); its name and type stay. A password or a sign-in left empty is kept. SMB and NFS are remounted with the new settings; an online connection tries the folder first, then its service is restarted. If the new settings fail, the previous ones are put back.
- Remote mounts live outside `/mnt`, so they are not offered as *Mounted drives*. Using them from the File Manager, shares and backups comes in a later step.

### Network

The **Network** page lists the interfaces (those of Docker and libvirt are hidden). The '...' menu of an Ethernet or bridge interface opens **Edit**: IPv4 and IPv6 tabs, IP assignment *Automatic* (DHCP for IPv4; router advertisements for IPv6 — on ifupdown, no DHCPv6 client is needed) or *Manual* (address, mask or prefix, gateway), DNS *Automatic* or *Manual*.

- **Automatic verification**: the change is applied at once, then the agent checks it for up to 30 seconds: the NAS carries the new address (or obtained a DHCP lease), the default route uses the new gateway, and the gateway answers (ping, or an entry in the neighbour table). If the check fails, the previous configuration is restored and the error says why. The original files are kept on disk (`/var/lib/ursanas/network-pending.json`) while the change is in progress: if the agent restarts in the middle of it, the previous configuration is restored at startup.
- **Open vSwitch (optional)**: the **Settings** button turns it on or off. Nothing is installed by default; turning it on installs the `openvswitch-switch` package (apt) if needed and starts its service. Turning it off stops the service (the package stays) and is refused while Open vSwitch bridges exist. The Linux bridges keep working for virtual machines either way.
- **Card on an Open vSwitch bridge**: in the ⋮ menu of an Ethernet card, *Use with Open vSwitch* creates a bridge (`ovsbr0`…) with the card as its uplink: the IP address, gateway and MAC address move to the bridge, so the NAS keeps its address and virtual machines can be plugged into the bridge from the Virtual machines page. It needs Open vSwitch enabled in the Settings, goes through the same automatic verification and rollback as an address change (the card must also be a port of the bridge), and is written as `90-ursanas-ovs-<bridge>.yaml` (netplan) or a marked block in `/etc/network/interfaces.d` (ifupdown). *Remove from Open vSwitch* on the bridge undoes it. The bridge itself is not editable: remove it, then edit the card.
- **Link aggregation**: *Create Link Aggregation* groups two or more free Ethernet cards into a bond (`bond0`…). Modes: *failover* (active-backup, the default, no switch setting), *adaptive load balancing* (balance-alb) or *LACP* (802.3ad, the switch ports must be set up for it). The IP address, gateway and MAC address of one card (the one carrying the gateway) move to the bond; the other cards lose their own settings. Same automatic verification and rollback as above, plus a check that every card is a member. Written as `90-ursanas-bond-<bond>.yaml` (netplan) or a marked block in `/etc/network/interfaces.d` (ifupdown, which installs `ifenslave` if needed). *Remove the aggregation* in the bond menu undoes it: the first card gets the addressing back, the others stay without settings. A bond is not editable directly: remove it, then create it again. A bond can itself be the uplink of an Open vSwitch bridge (*Use with Open vSwitch* in its menu): the cards stay in the bond, the bond becomes a port of the bridge, and it cannot be removed while it is one.
- **Responsibility**: UrsaNAS cannot know whether *your computer* can still reach the NAS (for example after changing the address you are connected through: reopen the NAS at its new address). If access is lost despite a successful check, correcting the network settings is up to you, from the machine's console. The dialog says so.
- **Supported systems**: *netplan* (Ubuntu Server) and *ifupdown* (Debian). Anything else (NetworkManager…) stays read-only. UrsaNAS only removes the addressing settings of the edited interface from the existing files and writes the new ones in its own file (`/etc/netplan/90-ursanas-<interface>.yaml` or `/etc/network/interfaces.d/ursanas-<interface>`), so other settings (match, MTU, other interfaces…) are kept. On Ubuntu with cloud-init, its network configuration is disabled when its file had to be touched.
- **Limits**: on ifupdown, manual DNS servers need a manual address (they are written to `dns-nameservers` and to `/etc/resolv.conf` when that is a regular file). Bonds, VLANs, Wi-Fi and Open vSwitch are not editable yet.

### Virtual machines

UrsaNAS drives **libvirt/KVM** (installed by the installer). The **Virtual machines** page checks that libvirt answers and explains what is missing:

```
sudo apt install qemu-system-x86 libvirt-daemon-system libvirt-clients virtinst ovmf swtpm
```

- **Create** a machine from an `.iso` found in a share or on a mounted disk: name, processors, memory, disk size (qcow2 file in `/var/lib/libvirt/images`) and network. UEFI is used when OVMF is installed.
- **Start, shut down** (ACPI request), **power off** (immediate) and **delete** (the virtual disk goes, the `.iso` stays).
- **Console** in the browser (noVNC). The screen of a machine only listens on `127.0.0.1`; the web interface relays it over an authenticated WebSocket. The noVNC viewer is bundled with the web interface (`wwwroot/lib/novnc`), so the console works without internet access.
- **Network**: choose a host bridge (for example `br0`) to put a machine on the local network, or the private libvirt network (NAT). The Open vSwitch bridges (when Open vSwitch is enabled in the Network settings) are listed too, and the machine is plugged into them with an `openvswitch` virtual port. UrsaNAS does not create bridges; create the bridge yourself (for example with NetworkManager or `/etc/network/interfaces`), because a mistake there cuts off remote access.
- If `/dev/kvm` is missing, the page warns: machines would be emulated and extremely slow. Enable virtualization in the BIOS, or nested virtualization when the NAS is itself a virtual machine (`Set-VMProcessor -VMName <name> -ExposeVirtualizationExtensions $true` on Hyper-V).

### Terminal

The **Terminal** icon in the top bar (shown to signed-in administrators) opens a window with a shell on the NAS (xterm.js, bundled in `wwwroot/lib/xterm`, so it works offline). It is a **root** shell: the agent starts `bash -l` on a pseudo-terminal and the web interface relays it over an authenticated WebSocket (administrators only, same-origin check). Opening and closing a session are written to the audit log; what is typed is not recorded. Closing the window ends the shell.

## Architecture

```
Browser ──HTTP :8080──▶ Nas.Web ──unix socket──▶ Nas.Agent (root) ──▶ zfs, smb, nfs, vsftpd, ACLs, copy…
                           │
                        SQLite (shares, history, alerts, audit log)
```

| Project | Role |
|---|---|
| `src/Nas.Agent` | Service running as root. Runs system commands and copies. Listens only on `/run/ursanas/agent.sock`. |
| `src/Nas.Web` | Interface and database. Never touches the system: it asks the agent for everything. Also runs the backup scheduler and the alerts. |
| `src/Nas.Contracts` | Shared types and validation rules (share names, ACLs, NFS, FTP, backups). |
| `external/Ursacopy` | Git submodule: the copy engine. |
| `external/UrsaUITools` | Git submodule: [Ursa.UITools](https://github.com/FrankJacket/UrsaUITools), the interface components that extend MudBlazor. |
| `tests/` | Unit tests (xUnit), including a check that every interface text has a French translation. |
| `deploy/` | Deployment script, install script, systemd services. |

Principle: **the agent is the only part with root rights**, and it validates everything the interface asks for. During a copy, the agent keeps progress in memory. The web app records the result in the history, then asks the agent to forget it.

## Install

### Install on a new machine

See [Install.md](Install.md): download the release package, extract it, run `sudo ./install.sh`. The package is built by the manual GitHub workflow *Release package* (Actions tab, Run workflow).

### Deploy from Windows (development)

The script publishes the agent and the interface for `linux-x64` (self-contained, no .NET needed on the target), copies them over SSH and restarts the services.

```powershell
git clone --recurse-submodules https://github.com/FrankJacket/UrsaNAS.git
cd UrsaNAS
./deploy/deploy.ps1                       # default target: ursanas@Ursa.mshome.net
./deploy/deploy.ps1 -Target user@my-nas   # another target
```

The interface is then at `http://<nas-address>:8080`. On the first visit, a form creates the first administrator.

If you cloned without `--recurse-submodules`:

```bash
git submodule update --init
```

## Develop

```bash
dotnet test        # all tests
```

Fake mode, with no real system and no root rights (handy on Windows):

```powershell
$env:URSANAS_FAKE = '1'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Backup__Roots__0 = 'C:/temp/mnt'        # folder standing in for /mnt
$env:Backup__RequireMountPoint = 'false'     # accept a plain folder as a disk
$env:Vm__OvmfDir = 'C:/temp/mnt'             # any existing folder: stands in for the UEFI firmware (OVMF)
$env:ASPNETCORE_URLS = 'http://localhost:5190'
dotnet run --project src/Nas.Agent --no-launch-profile
# in another terminal:
$env:ASPNETCORE_URLS = 'http://localhost:5199'; $env:Agent__Url = 'http://localhost:5190/'
dotnet run --project src/Nas.Web --no-launch-profile
```

In fake mode, three virtual machines are simulated in memory (`debian-12` running, `windows-11` and `alpine-test` shut off, with disks, network cards and snapshots): starting, stopping, editing the hardware, snapshots and creating a machine all work, and everything is forgotten when the agent stops. To also offer bridges in the machine forms, point `Vm__NetDir` to a folder containing `br0/bridge`. Other system commands answer "OK" and do nothing. For the list of backup disks to be non-empty, set `Backup__MountInfo` to a file in the `/proc/self/mountinfo` format declaring your fake disk. `Backup__RequireMountPoint=false` alone is enough to test a copy, but it does not make the disk appear in the list.

In Visual Studio, the `fake` launch profile of both projects does the same (agent on port 5002 with `URSANAS_FAKE=1`, web interface on port 5018 talking to it). Create `C:\temp\mnt`, set `Nas.Agent` and `Nas.Web` as multiple startup projects ("Configure Startup Projects…"), select the `fake` profile for both, then press F5.

In Visual Studio, a first-chance `NavigationException` at start-up on `/setup` is normal (Blazor's way of redirecting while rendering on the server, when no account exists yet). Continue, or untick "Break when this exception type is thrown".

### Interface texts

Texts are written in English in the code, for example `L["Save"]`. Each text needs its translation in `src/Nas.Web/Localization/French.cs`, otherwise a test fails.

## Useful configuration

| Key | Default | Effect |
|---|---|---|
| `Backup:Roots` | `/mnt`, `/media`, `/run/media` | Folders under which a backup disk may be mounted |
| `Backup:RequireMountPoint` | `true` | `false` accepts a plain folder (development only) |
| `Backups:IntervalSeconds` | `20` | How often the backup scheduler runs |
| `DataDir` | `/var/lib/ursanas-web` | Folder holding the interface's SQLite database |
| `Docker:StacksDir` | `/var/lib/ursanas/stacks` | Where Compose stacks are stored (agent) |
| `Docker:IntervalSeconds` | `10` | How often the web interface checks whether Docker is available |
| `Docker:Binary` | `docker` | Docker command to run (`docker-missing` simulates a machine without Docker in fake mode) |
