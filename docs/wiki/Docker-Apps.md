# Docker Apps

Docker lets you run small, ready-made applications on the NAS — things like a photo gallery, a media server, or a download manager — without installing anything by hand. Each application runs in its own self-contained **container**, separate from the rest of the system.

The **Docker** icon on the [desktop](The-Desktop) only appears once Docker is installed and running. Its window has a menu down the left side with four sections — **Containers**, **Images**, **Stacks** and **Volumes and networks** — and the section you pick is shown on the right. If the window is made narrow, the menu shrinks to icons only.

## Key ideas, kept simple

- **Images** are the installable "packages" for applications — think of an image as the installer for a container.
- **Containers** are running (or stopped) copies of an image — the actual applications.
- **Stacks** are groups of containers that are meant to run together as one application.
- **Volumes and networks** are how containers store files persistently and talk to each other and to the network; you'll rarely need to touch these directly unless an application's instructions ask you to.

## A folder a container uses

If a container needs access to your files (for example, a media server reading your video folder), point it at the folder when you set it up. Keep in mind: a container that reads a NAS folder has **full access** to it, regardless of the [permissions](Advanced-Permissions) you've set up for people connecting to that share — permissions only apply to network connections, not to containers running on the NAS itself.
