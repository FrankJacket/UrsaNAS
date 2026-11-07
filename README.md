# UrsaNAS

UrsaNAS is a home NAS: a web interface (English / French) to manage disks, ZFS storage, file sharing, accounts, monitoring and backups to a USB disk on a Debian machine.

It is written in C# (.NET 10) with MudBlazor, SQLite and Dapper.

## What you can do with it

The menu is organised in groups. The Docker group only appears when Docker is installed and running.

| Group | Pages | Purpose |
|---|---|---|
| **Storage** | Disks, Pools, Datasets, Backups | Inspect disks, create ZFS pools and datasets, take snapshots, back up to a USB disk |
| **Sharing** | Shares, Folders, Services | Share folders over SMB, NFS and FTP, set per-folder permissions (ACLs), start or stop services |
| **Docker** | Containers, Images, Stacks, Volumes and networks | Run and monitor containers, manage images, Compose stacks, volumes and networks |
| **Accounts** | Users, Groups, Admins | NAS accounts, groups, web interface administrators |
| **Monitoring** | Dashboard, Alerts, Audit log | Health overview, alerts (with email), log of actions |

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

UrsaNAS does not install Docker. It checks every few seconds whether Docker is installed and its service answers, and shows the **Docker** menu group only when it does. Until then, the `/docker` page (linked from *Services*) explains how to install or start it:

```bash
sudo apt install docker.io docker-compose
```

- **Containers**: list with state, ports, CPU and memory; start, stop, restart, delete; logs; run a simple container (image, ports, environment variables, volumes, network, restart policy) and edit it later in the same dialog. A restart-policy change is applied in place; any other change recreates the container, and the previous one is restored if the new one cannot start. Containers managed by a Compose stack are edited through their stack.
- **Images**: list, download (pull), delete, clean unused images.
- **Stacks**: Docker Compose files kept on the NAS (`/var/lib/ursanas/stacks/<name>/compose.yml`). Edit them in the browser, then start, stop, update (pull + up) or delete the stack. A file is checked with `docker compose config` before it is saved, so an invalid file never replaces a working one.
- **Volumes and networks**: list, create, delete.
- **NAS folders as volumes**: a button inserts the real path of a share folder (picked in the file tree) into the container dialog or the compose file.

Docker bypasses the share permissions (ACLs): a container that mounts a NAS folder reads and writes it with full rights. The interface says so where it matters. Names, images, ports, variables and volumes are validated before they reach the `docker` command, which is always run without a shell and never receives a value that could be read as an option.

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
| `tests/` | Unit tests (xUnit), including a check that every interface text has a French translation. |
| `deploy/` | Deployment script, install script, systemd services. |

Principle: **the agent is the only part with root rights**, and it validates everything the interface asks for. During a copy, the agent keeps progress in memory. The web app records the result in the history, then asks the agent to forget it.

## Install

### Target requirements

Debian 13 with ZFS (for pools and datasets) and SSH access with `sudo`. The install script installs NFS, vsftpd and openssl itself, leaving the services stopped until they are enabled from the *Services* page. For exFAT backup disks, install `exfatprogs`.

### Deploy from Windows

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

The Ursacopy repository is private: your GitHub account needs access to it.

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
$env:ASPNETCORE_URLS = 'http://localhost:5190'
dotnet run --project src/Nas.Agent --no-launch-profile
# in another terminal:
$env:ASPNETCORE_URLS = 'http://localhost:5199'; $env:Agent__Url = 'http://localhost:5190/'
dotnet run --project src/Nas.Web --no-launch-profile
```

In fake mode, system commands answer "OK" and do nothing. For the list of backup disks to be non-empty, set `Backup__MountInfo` to a file in the `/proc/self/mountinfo` format declaring your fake disk. `Backup__RequireMountPoint=false` alone is enough to test a copy, but it does not make the disk appear in the list.

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
