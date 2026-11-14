# Remote Storage

**Remote storage** connects your NAS to storage that lives somewhere else: another server on your network, or an online service. Once connected, it behaves like a local disk — you can add it as a source in the [File Manager](File-Manager) or use it as the target of a share.

## Adding a connection

Click **Add a connection** and choose the type:

- **SMB** or **NFS** — another server or NAS on your network.
- **OneDrive** or **Dropbox** — an online storage account. You'll be asked to sign in to that service to allow the connection.

Fill in the server address (or sign in, for an online service) and the folder you want to use, then save.

## Using it

A connection can be **Mounted** (connected and ready to use) or **Not connected**. Use the **Mount** / **Unmount** buttons to connect or disconnect it on demand — for example, unmount a connection you only need occasionally.

Removing a connection here only makes UrsaNAS forget about it; it never deletes anything from the remote server or account.
