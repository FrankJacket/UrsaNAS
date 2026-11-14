# Advanced Permissions

Most shares work fine with the simple **Read-only** / **Read/write** choice described in [Sharing Files](Sharing-Files). Turn on **Advanced permissions** for a share only when different people need different access to it — for example, everyone can read a folder but only one person can change it, or a subfolder needs to stay private to a few people.

You'll find this on a share's settings, or by clicking **Permissions** in the [File Manager](File-Manager) on any folder inside it.

## How it works

A folder either:

- **Inherits** its permissions from its parent folder (the default — simplest, and usually what you want), or
- Has its own permissions, which you set by clicking **Add a permission**.

Each permission you add says: this **user or group** is **allowed** (or **denied**) **this level of access**:

- **Read** — can see and open files.
- **Modify** — can also add, change and delete files.
- **Full control** — can also change who else has access.

That's normally all you need. The full list of fine-grained boxes underneath (traverse, read attributes, and so on) is there for unusual cases; leave them alone unless you specifically know you need one.

## A tip on sub-folders

Only break a folder's inheritance (give it its own permissions, separate from its parent) when that specific folder genuinely needs different access than the rest of the share. Keeping inheritance on everywhere else makes it much easier to see at a glance who can access what — the **File Manager** marks any folder where inheritance is off, so you can always find where the exceptions are.

## Note on FTP and NFS

Advanced permissions only affect access over **SMB** (the Windows/macOS/Linux way of connecting — see [Sharing Files](Sharing-Files)). Over NFS and FTP, access is controlled by that share's own list of allowed users or network addresses instead.
