# Backups

A backup job copies chosen folders to an external disk on a schedule, so you still have your files if something happens to the NAS itself. It's a plain copy of your files, readable on any computer — not a restorable system image, and it doesn't preserve advanced permissions.

## Setting up a backup disk

1. Plug an external disk into the NAS.
2. Click **New backup job** and give it a name. The job's settings are split into four tabs:
   - **What to back up** — pick shares, folders or individual files, across all your shares. Here you can also turn on copying from a ZFS snapshot (so files that change during the copy are copied consistently), verifying each copied file, and list files or folders to skip.
   - **Where to copy** — the backup disk and the folder on it.
   - **Schedule** — when the job runs (see **Running it** below).
   - **Retention** — how long deleted and replaced files are kept (see the end of this page).
3. The card for your new job will show the disk as **"Disk not initialised"**. Click **Initialise the disk** — this marks the disk as the one this job backs up to, so UrsaNAS can recognise it later. It doesn't erase anything already on it.

## Running it

- **Run now** starts a copy immediately.
- Or set a schedule in the job's settings: **every day** at a chosen time, or **whenever the disk is connected** (handy for a disk you plug in occasionally and unplug when done).
- While a copy runs, its card shows progress: how many files were copied, left unchanged, removed, or had errors.

## If you replace the disk

If a job's card shows **"Another disk"**, it means the disk currently plugged in isn't the one this job expects — for example, because you swapped it for a new one. Check that you really do want to use this disk for this job, then click **Use this disk**.

## History

The **History** section at the bottom of the page lists every past run, with its result and how much was copied — useful for confirming backups are actually happening on schedule.

## What UrsaNAS writes on the backup disk

Besides your copied files, a backup disk holds two small, hidden items that are worth knowing about:

- **A marker file**, written once when you click **Initialise the disk**. It's how UrsaNAS recognises "this is the right disk for this job" the next time it's plugged in — it's also why a disk shows as **"Another disk"** if you plug in a different one that was never initialised for this job. Don't delete it.
- **A "Trash" folder**, where UrsaNAS puts files for a little while instead of erasing them right away: a file that was deleted from the source, or replaced by a newer version, goes here rather than disappearing immediately. This gives you a safety net to recover a file you removed or overwrote by mistake. Each job's setting **"Keep deleted and replaced files (days)"** decides how long they stay there before being cleaned up for good — set it to `0` if you'd rather they be removed immediately instead.
