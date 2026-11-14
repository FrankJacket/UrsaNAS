# Sharing Files

A **share** makes a folder on the NAS reachable from your other devices over the network — this is the whole point of a NAS. This page covers the **Shares** and **Services** pages.

## Creating a share

On the **Shares** page, click **Add a share**. Give it a name, point it at a folder (a dataset you created under [Storage](Storage), another disk, or a fresh folder), and choose how open it is:

- **Read-only**: anyone it's shared with can see and download files, but not change them.
- **Read/write**: they can also add, change and delete files.
- **Advanced permissions**: instead of one setting for everyone, you decide exactly who can do what. See **[Advanced Permissions](Advanced-Permissions)**.

Right after creating a share, UrsaNAS opens its permissions for you, since a brand-new share isn't accessible to anyone yet.

## Connecting to a share

Once created, a share shows up on the network under its name. How you open it depends on the device:

- **Windows**: open File Explorer and type `\\<NAS address>\<share name>`.
- **macOS**: in Finder, use *Go → Connect to Server* and type `smb://<NAS address>/<share name>`.
- **Linux, phones, media players**: look for "network drive" or "SMB" in the device's file browser.

You'll be asked for the username and password of a [user account](Accounts) that has access to the share.

## Services: SMB, NFS, FTP

The **Services** page lists the three ways a device can connect to your shares:

- **SMB** is what Windows, macOS and most modern devices use — it's always on.
- **NFS** is mainly used by Linux and Unix systems.
- **FTP** is an older, simple way to transfer files, useful for devices or software that only support it.

NFS and FTP are off by default; turn them on here only if something you own actually needs them. Each share decides for itself whether it's also offered over NFS or FTP, in the share's own settings.

If you turn FTP on, also set its options here: whether a connection must be encrypted (recommended if FTP is reachable from outside a fully trusted network), and which range of ports it uses — your network's firewall needs to allow that range.
