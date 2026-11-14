# Virtual Machines

A virtual machine (VM) is a full, separate computer that runs inside your NAS — with its own operating system, like Windows or another Linux. It's useful for running software that needs a dedicated, isolated environment, separate from the NAS itself.

## Before you start

This page will tell you if something needs to be enabled first — for example, a setting in your machine's BIOS/UEFI called **virtualization**, which needs to be turned on for virtual machines to run at a usable speed. The page explains what to do and re-checks automatically every few seconds.

## Creating a machine

Click **Create a machine**, give it processors, memory and disk space, and point it at an installation image (an `.iso` file) for the operating system you want to install. The new machine starts up from that image, just like a brand-new physical computer would — you install its operating system the normal way, through its own console.

## Using a machine

- **Console** opens a screen for the machine, so you can see and interact with it as if you were sitting in front of it.
- **Start** / **Shut down** work like the power button on a real computer — shut down is the polite way, giving the machine's own operating system a chance to close things properly.
- **Power off** cuts power immediately, like pulling the plug: only use it if the machine is stuck and won't shut down normally, since anything unsaved inside it will be lost.
- **Delete** removes the machine and its virtual disk for good; the installation image itself is kept, so you can create another machine from it later.
