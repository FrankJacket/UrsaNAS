# Storage

This area is about the disks inside (or plugged into) the NAS, and how they're organised to hold your files. It covers three pages: **Disks**, **ZFS pools**, and **Datasets**.

## Disks

The **Disks** page lists every disk the NAS can see, with its size, health and whether it's currently in use. From here you can:

- **Partition and format** a new disk to prepare it for use.
- **Erase** a disk you no longer need, wiping everything on it.
- Check a disk's health at a glance — a disk marked anything other than "Normal" is worth keeping an eye on, and worth backing up from and replacing if it says "Failing".

A disk already used by the system, or already part of a pool, is marked as such and most of these actions are unavailable for it — that's expected.

## ZFS pools

A **pool** groups several disks together so they behave like one large, more reliable storage space. When you create a pool, you choose how the disks protect each other against failure:

| Type | What it means for you |
|---|---|
| **Mirror** | Every disk holds a full copy of your data. Simple and safe, but you only get the capacity of one disk (out of two, or more). |
| **RAIDZ1** | Needs at least 3 disks. The pool can lose **one** disk without losing any data. |
| **RAIDZ2** | Needs at least 4 disks. The pool can lose **two** disks at once without losing any data. |

Once created, a pool shows its used/free space and overall health (Online, Degraded, or Faulted). **Degraded** means a disk needs attention but your data is still safe; **Faulted** means act immediately. The **Scrub** button asks the pool to check all of its data for silent corruption — safe to run any time, and worth doing occasionally.

Creating a pool erases everything on the disks you choose for it, so only pick disks that are empty or no longer needed.

## Datasets

A **dataset** is a folder-like unit inside a pool. You'll usually create one dataset per share you plan to set up (see [Sharing Files](Sharing-Files)) — for example one for photos, one for documents. Datasets created inside a pool benefit from that pool's protection automatically.

Datasets also support **snapshots**: a snapshot freezes the current state of a dataset so you can look back at it later, without taking up much space right away (it only grows as files change). Snapshots are not a substitute for a real backup to a separate disk — see [Backups](Backups) for that.
