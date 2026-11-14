# System Settings

This page is about keeping UrsaNAS itself healthy and up to date — not your files. It has three tabs: **Updates**, **Scheduling**, and **Configuration backup**.

## Updates

Click **Check for updates** to see if a newer version of UrsaNAS is available, and a separate check for updates to the underlying system software. If one is found, **Update now** installs it — the interface becomes briefly unreachable while the services restart, which is normal.

Some system updates are marked **"Manual only"**: these restart an important service or change the core of the system, so they're never installed automatically, only when you choose to.

## Scheduling

Instead of checking and installing by hand, you can automate it here:

- **Check for updates** — how often UrsaNAS looks for new versions on its own (daily, weekly, or never).
- **Automatic installation** — whether UrsaNAS (and/or system software updates) should install what the last check found, on a day and time you choose. You can also allow it to restart the machine automatically afterwards, if a restart turns out to be needed.

Times you set here are the NAS's own clock, shown at the top of this tab.

## Configuration backup

This downloads a small file with your NAS's **setup** — shares, local accounts, network settings, backup job definitions, and so on — so you can quickly set everything back up again if the NAS ever needs a full reset. It does **not** contain your actual files, and it never includes passwords (you'll re-enter those after a restore).

Click **Download**, optionally turning on **Protect the ZIP file with a password** first if you'd rather not leave the file unprotected. Keep the downloaded file somewhere safe, separate from the NAS itself.
