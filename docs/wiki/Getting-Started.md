# Getting Started

## What you need

- A computer (or virtual machine) with nothing important on it yet — the installer takes it over to run UrsaNAS.
- A network cable plugged in, and an internet connection (needed only during installation).
- At least one empty disk, if you want to store files beyond what the NAS's own system disk offers.

## Installing

Installing UrsaNAS itself means preparing the machine's operating system and running a command in a terminal — see **[Installing UrsaNAS](Installing-UrsaNAS)** for the exact steps. If that's not something you want to do yourself, it only needs to be done once by whoever is comfortable with it; everything after this point is done from the web interface.

Once it's installed, it takes a few minutes to finish. At the end, it prints the network address of your new NAS, for example `http://192.168.1.50:8080`.

## Signing in for the first time

1. Open the address the installer printed in a web browser, on a device connected to the same network.
2. The first time, you'll be asked to **create the first administrator** account: choose a username and a strong password. This account can do anything on the NAS, so keep the password safe.
3. You're in. The **Dashboard** is the first thing you see.

## What to do next

A sensible order for setting things up:

1. **[Storage](Storage)** — prepare your disks so they're ready to hold files.
2. **[Sharing Files](Sharing-Files)** — create a share for your folders so other devices can reach them.
3. **[Accounts](Accounts)** — create a user account for each person who will connect to a share.

From there, explore the rest as you need it: [Backups](Backups), [Remote Storage](Remote-Storage), [Virtual Machines](Virtual-Machines), [Docker Apps](Docker-Apps).

## A note on safety

The interface is reachable over plain HTTP (not the padlock-and-HTTPS kind). This is fine on your own home network, but UrsaNAS should never be made reachable from the internet directly.
