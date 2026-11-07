using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>
/// Gestionnaire de fichiers sur un disque monté (USB…) : parcourir, créer un dossier, renommer, déplacer, copier, supprimer.
/// Le disque est repéré par son point de montage, validé comme pour les sauvegardes (sous /mnt, /media… et réellement monté) ;
/// les chemins sont relatifs à ce point de montage, sans « .. » et sans lien symbolique. Pas de permissions : NTFS et exFAT n'ont pas d'ACL.
/// </summary>
public sealed class DiskFilesService(BackupService backup)
{
    public static bool IsDisk(string location) => location.Contains('/'); // un nom de partage n'a jamais de barre

    string Root(string dir) => backup.ValidateTarget(dir);

    static string CheckPath(string? path)
    {
        if (AclRules.ValidateFolderPath(path) is { } e) throw new OperationException(e);
        return path ?? "";
    }

    public FolderListing List(string dir, string path, bool withFiles)
    {
        var root = Root(dir);
        path = CheckPath(path);
        var cur = path.Length == 0 ? root : AclService.Dir(root, path);
        var info = new DirectoryInfo(cur);
        var folders = info.EnumerateDirectories().Where(d => d.LinkTarget is null && AclRules.ValidSegment(d.Name))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(d => new FolderEntry(d.Name, d.LastWriteTimeUtc)).ToList();
        var files = withFiles
            ? info.EnumerateFiles().Where(f => f.LinkTarget is null && AclRules.ValidSegment(f.Name))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => new FileEntry(f.Name, f.Length, f.LastWriteTimeUtc)).ToList()
            : null;
        return new FolderListing(path, folders, files);
    }

    public void CreateFolder(CreateFolderRequest r)
    {
        var root = Root(r.Share);
        var parent = AclService.Dir(root, CheckPath(r.Path));
        if (AclRules.ValidateNewFolderName(r.Name) is { } e) throw new OperationException(e);
        var target = Path.Combine(parent, r.Name);
        if (Exists(parent, r.Name)) throw new OperationException(Msg.F("A folder named “{0}” already exists.", r.Name));
        try { Directory.CreateDirectory(target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new OperationException(Msg.F("{0} failed ({1}): {2}", "Creating the folder", 1, ex.Message)); }
    }

    static bool Exists(string dir, string name, string? except = null) =>
        new DirectoryInfo(dir).EnumerateFileSystemInfos().Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(x.FullName, except, StringComparison.Ordinal));

    (string Source, string Target) Plan(string dir, string path, string destParent, string newName, bool isFile)
    {
        var root = Root(dir);
        path = CheckPath(path);
        destParent = CheckPath(destParent);
        if (path.Length == 0) throw new OperationException(Msg.F("The share root has no parent folder."));
        if (AclRules.ValidateNewFolderName(newName) is { } e) throw new OperationException(e);
        if (!isFile && (destParent == path || destParent.StartsWith(path + "/", StringComparison.Ordinal)))
            throw new OperationException(Msg.F("A folder cannot be moved or copied into itself."));
        var source = AclService.Dir(root, path, isFile);
        var destDir = AclService.Dir(root, destParent);
        if (Exists(destDir, newName, source))
            throw new OperationException(isFile ? Msg.F("An item named “{0}” already exists.", newName) : Msg.F("A folder named “{0}” already exists.", newName));
        return (source, Path.Combine(destDir, newName));
    }

    public void Move(MoveFolderRequest r)
    {
        var (source, target) = Plan(r.Share, r.Path, r.DestParent, r.NewName, r.IsFile);
        try { if (r.IsFile) File.Move(source, target); else Directory.Move(source, target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new OperationException(Msg.F("{0} failed ({1}): {2}", "Moving the folder", 1, e.Message)); }
    }

    public void Copy(CopyFolderRequest r)
    {
        var (source, target) = Plan(r.Share, r.Path, r.DestParent, r.NewName, r.IsFile);
        try { if (r.IsFile) File.Copy(source, target); else AclService.CopyDir(new DirectoryInfo(source), target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new OperationException(Msg.F("{0} failed ({1}): {2}", "Copying the folder", 1, e.Message)); }
    }

    public void Delete(string dir, string path, bool isFile)
    {
        var root = Root(dir);
        path = CheckPath(path);
        if (path.Length == 0) throw new OperationException(Msg.F("The share root cannot be deleted."));
        var target = AclService.Dir(root, path, isFile);
        try { if (isFile) File.Delete(target); else Directory.Delete(target, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new OperationException(Msg.F("{0} failed ({1}): {2}", "Deleting the folder", 1, e.Message)); }
    }
}
