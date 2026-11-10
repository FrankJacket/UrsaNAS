using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>
/// Copie ou déplace un dossier ou un fichier d'un emplacement à un autre : partage vers partage, partage vers disque monté (USB…), etc.
/// Un emplacement est un nom de partage ou, s'il contient une barre, le point de montage d'un disque. Un déplacement entre deux
/// emplacements est une copie suivie de la suppression de l'original : rien n'est supprimé si la copie a échoué.
/// </summary>
public sealed class TransferService(AclService acl, BackupService backup)
{
    async Task<string> RootAsync(string location, CancellationToken ct)
    {
        if (DiskFilesService.IsDisk(location)) return backup.ValidatePlace(location);
        if (Rules.ValidateShareName(location) is { } e) throw new OperationException(e);
        return await acl.SharePathAsync(location, ct);
    }

    public Task CopyAsync(CopyFolderRequest r, CancellationToken ct = default) =>
        TransferAsync(r.Share, r.DestShare!, r.Path, r.DestParent, r.NewName, r.IsFile, move: false, ct);

    public Task MoveAsync(MoveFolderRequest r, CancellationToken ct = default) =>
        TransferAsync(r.Share, r.DestShare!, r.Path, r.DestParent, r.NewName, r.IsFile, move: true, ct);

    async Task TransferAsync(string from, string to, string path, string destParent, string newName, bool isFile, bool move, CancellationToken ct)
    {
        foreach (var p in new[] { path, destParent })
            if (AclRules.ValidateFolderPath(p) is { } e1) throw new OperationException(e1);
        if (AclRules.ValidateNewFolderName(newName) is { } e2) throw new OperationException(e2);
        if (path.Length == 0) throw new OperationException(Msg.F("The share root has no parent folder."));

        var srcRoot = await RootAsync(from, ct);
        var dstRoot = await RootAsync(to, ct);
        var source = AclService.Dir(srcRoot, path, isFile);
        var destDir = AclService.Dir(dstRoot, destParent);
        var target = Path.Combine(destDir, newName);
        if (!isFile && IsInside(target, source)) throw new OperationException(Msg.F("A folder cannot be moved or copied into itself."));
        if (new DirectoryInfo(destDir).EnumerateFileSystemInfos().Any(x => string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase)))
            throw new OperationException(isFile ? Msg.F("An item named “{0}” already exists.", newName) : Msg.F("A folder named “{0}” already exists.", newName));

        var what = move ? "Moving the folder" : "Copying the folder";
        try
        {
            try { if (isFile) File.Copy(source, target); else AclService.CopyDir(new DirectoryInfo(source), target); }
            catch
            {
                TryDelete(target, isFile); // une copie interrompue ne laisse pas de morceau derrière elle
                throw;
            }
            if (!DiskFilesService.IsDisk(to))
                await acl.ReceiveCopyAsync(to, (destParent.Length == 0 ? "" : destParent + "/") + newName, target, ct);
            if (move) { if (isFile) File.Delete(source); else Directory.Delete(source, recursive: true); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new OperationException(Msg.F("{0} failed ({1}): {2}", what, 1, e.Message)); }
    }

    static bool IsInside(string child, string parent) =>
        (Path.GetFullPath(child).Replace('\\', '/') + "/").StartsWith(Path.GetFullPath(parent).Replace('\\', '/').TrimEnd('/') + "/", StringComparison.Ordinal);

    static void TryDelete(string path, bool isFile)
    {
        try { if (isFile) File.Delete(path); else if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
