# Troubleshooting

## I can't open the interface at all

- Double check the address — it may have changed if the NAS's network settings changed.
- Make sure the NAS is powered on and connected to the network.
- If a firewall runs on your network or on the NAS, make sure port 8080 is allowed.

## The interface loads, but says "Agent unreachable"

This means the part of UrsaNAS that actually talks to your hardware isn't responding right now. It usually recovers on its own after a restart of the NAS. If it doesn't, ask whoever manages the NAS for you to look at it — this points to something that needs attention at the system level, beyond what the web interface itself can fix.

## A feature is greyed out or missing

Some features (Virtual Machines, Docker Apps, ZFS pools, FTP) depend on extra software that isn't installed by default, or on hardware support (like virtualization) that isn't turned on. The page for that feature explains exactly what's missing and what to do about it — read the message shown there first.

## A disk shows as "Failing" or a pool as "Degraded" or "Faulted"

See [Storage](Storage) for what these mean. In short: back up what's on it if you haven't recently, and plan to replace the disk soon — "Faulted" means sooner rather than later.

## I forgot an administrator password

Ask another administrator to open the **[Accounts](Accounts)** page and set a new password for you. If there's only one administrator and nobody can sign in, this needs to be fixed at the system level — contact whoever manages the NAS for you.

## Something changed and I don't know why

Check the **[Audit log](Alerts-and-Activity)** — it records what changed, when, and by whom.
