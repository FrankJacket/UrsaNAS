using System.Text;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>
/// Écrit les permissions avancées dans les ACL NT de Samba (<c>vfs_acl_xattr</c>) avec <c>smbcacls</c>, via un compte SMB
/// réservé à l'agent (<c>admin users</c> du partage). Aucun chemin brut ne vient de l'extérieur : le partage et le chemin
/// relatif sont validés, et le serveur Samba confine lui-même l'accès au partage.
/// </summary>
public sealed class AclService(ICommandRunner run, UserService users, GroupService groups, IConfiguration cfg)
{
    public const string ServiceUser = "ursanas-acl";

    string AuthFile => cfg["Acl:AuthFile"] ?? "/var/lib/ursanas/acl-auth";

    /// <summary>Crée au besoin le compte SMB réservé (mot de passe aléatoire dans un fichier lisible par root seul) ; renvoie son SID.</summary>
    public async Task<string> EnsureServiceAccountAsync(CancellationToken ct = default)
    {
        var exists = (await run.RunAsync("id", ["-u", ServiceUser], ct: ct)).ExitCode == 0;
        if (!exists)
            Check(await run.RunAsync("useradd", ["--system", "--no-create-home", "--shell", "/usr/sbin/nologin", ServiceUser], ct: ct), "Creating the account");

        if (!exists || !File.Exists(AuthFile))
        {
            var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
            Check(await run.RunAsync("smbpasswd", ["-a", "-s", ServiceUser], $"{password}\n{password}\n", ct), "Creating the Samba account");
            Directory.CreateDirectory(Path.GetDirectoryName(AuthFile)!);
            await using (File.Create(AuthFile)) { }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(AuthFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await File.WriteAllTextAsync(AuthFile, $"username={ServiceUser}\npassword={password}\n", ct);
        }
        return await UserSidAsync(ServiceUser, ct);
    }

    /// <summary>« User SID : S-1-5-21-… » dans la sortie de <c>pdbedit -v</c>.</summary>
    public static string? ParseUserSid(string pdbedit)
    {
        foreach (var line in pdbedit.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("User SID", StringComparison.Ordinal)) continue;
            var sid = t[(t.IndexOf(':') + 1)..].Trim();
            if (sid.StartsWith("S-1-", StringComparison.Ordinal)) return sid;
        }
        return null;
    }

    async Task<string> UserSidAsync(string name, CancellationToken ct)
    {
        var r = await run.RunAsync("pdbedit", ["-u", name, "-v"], ct: ct);
        return (r.ExitCode == 0 ? ParseUserSid(r.StdOut) : null)
            ?? throw new OperationException(Msg.F("User “{0}” does not exist.", name));
    }

    async Task<Func<AclSubjectKind, string, string>> ResolverAsync(IReadOnlyList<AclRule> all, CancellationToken ct)
    {
        var map = new Dictionary<(AclSubjectKind, string), string>();
        foreach (var (kind, name) in all.Select(r => (r.Kind, r.Name)).Distinct())
        {
            if (kind == AclSubjectKind.User)
            {
                if (!(await users.ListAsync(ct)).Any(u => u.Name == name)) throw new OperationException(Msg.F("User “{0}” does not exist.", name));
                map[(kind, name)] = await UserSidAsync(name, ct);
            }
            else
            {
                var gid = await groups.GidAsync(name, ct) ?? throw new OperationException(Msg.F("Group “{0}” does not exist.", name));
                map[(kind, name)] = $"S-1-22-2-{gid}";
            }
        }
        return (k, n) => map[(k, n)];
    }

    static string Validate(string share, string path, params IReadOnlyList<AclRule>[] ruleSets)
    {
        if (Rules.ValidateShareName(share) is { } e1) throw new OperationException(e1);
        if (AclRules.ValidateFolderPath(path) is { } e2) throw new OperationException(e2);
        foreach (var rs in ruleSets)
            if (AclRules.ValidateRules(rs) is { } e3) throw new OperationException(e3);
        return "/" + path;
    }

    public async Task SetFolderAsync(SetFolderAclRequest req, CancellationToken ct = default)
    {
        var smbPath = Validate(req.Share, req.Path, [req.Rules, .. req.Ancestors]);
        var svc = await EnsureServiceAccountAsync(ct);
        var resolve = await ResolverAsync([.. req.Rules, .. req.Ancestors.SelectMany(a => a)], ct);
        var aces = AclCompiler.CompileFolder(req.Rules, req.Ancestors, resolve, svc);

        // 1. L'ACL entière du dossier (règles propres + héritage des parents).
        Check(await Smbcacls(req.Share, smbPath, ["--set", string.Join(',', aces)], ct), "Setting permissions");
        // 2. Héritage : « allow » le laisse actif ; « remove » protège le dossier (aucune ACE héritée n'y est écrite).
        var inherit = await Smbcacls(req.Share, smbPath, ["--inherit=" + (req.Inherit ? "allow" : "remove")], ct);
        if (!(inherit.StdOut + inherit.StdErr).Contains("Already set", StringComparison.Ordinal)) // déjà dans l'état voulu : sans objet
            Check(inherit, "Setting permissions");
        // 3. Propagation aux éléments déjà présents : « modifier » l'ACE de service à l'identique suffit à la déclencher,
        //    smbcacls refusant --set et --propagate-inheritance ensemble.
        Check(await Smbcacls(req.Share, smbPath, ["--modify", AclCompiler.ServiceAce(svc), "--propagate-inheritance"], ct), "Applying permissions to existing items");
    }

    /// <summary>
    /// Passage d'un partage existant au mode permissions : ce qui existe déjà n'a pas d'ACL et Samba en fabrique une à partir
    /// des droits Unix (« tous les NAS peuvent écrire »), que la propagation figerait. On la remplace par une ACL minimale
    /// (l'agent seul) avant que les règles du dossier racine n'y soient propagées.
    /// </summary>
    public async Task ResetTreeAsync(string share, string path = "", CancellationToken ct = default)
    {
        Validate(share, path);
        var svc = await EnsureServiceAccountAsync(ct);
        var root = await SharePathAsync(share, ct);
        Dir(root, path, File.Exists(Path.Combine(root, path)));
        var sddl = $"D:(A;;FA;;;{svc})";
        var batch = new List<string>();

        async Task Flush()
        {
            if (batch.Count == 0) return;
            var file = Path.Combine(Path.GetDirectoryName(AuthFile)!, $"acl-restore-{Guid.NewGuid():N}");
            try
            {
                await File.WriteAllBytesAsync(file, Encoding.Unicode.GetBytes(string.Join("\r\n", batch.SelectMany(p => new[] { p, sddl })) + "\r\n"), ct);
                Check(await Smbcacls(share, "/", ["--restore=" + file], ct), "Resetting permissions");
            }
            finally { File.Delete(file); }
            batch.Clear();
        }

        foreach (var rel in Walk(root, path))
        {
            batch.Add(rel);
            if (batch.Count >= 2000) await Flush();
        }
        await Flush();
    }

    /// <summary>Tout ce qui est sous <paramref name="root"/>, en chemins relatifs ; ni lien symbolique, ni nom aux caractères douteux.</summary>
    static IEnumerable<string> Walk(string root, string start = "")
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var stack = new Stack<string>();
        if (start.Length > 0)
        {
            yield return start;
            if (!Directory.Exists(Path.Combine(root, start))) yield break; // un fichier : rien dessous
        }
        stack.Push(start);
        while (stack.Count > 0)
        {
            var rel = stack.Pop();
            var dir = rel.Length == 0 ? root : Path.Combine(root, rel);
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
            {
                if (!AclRules.ValidSegment(entry.Name) || entry.LinkTarget is not null) continue;
                var childRel = rel.Length == 0 ? entry.Name : rel + "/" + entry.Name;
                yield return childRel;
                if (entry is DirectoryInfo) stack.Push(childRel);
            }
        }
    }

    /// <summary>Dossier réel d'un chemin relatif ; refuse tout segment absent ou qui serait un lien symbolique.</summary>
    static string Dir(string root, string rel, bool file = false)
    {
        var cur = root;
        var segs = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segs.Length; i++)
        {
            cur = Path.Combine(cur, segs[i]);
            FileSystemInfo d = file && i == segs.Length - 1 ? new FileInfo(cur) : new DirectoryInfo(cur); // « file » : le dernier élément est un fichier
            if (!d.Exists || d.LinkTarget is not null) throw new OperationException(Msg.F("Invalid folder path."));
        }
        return cur;
    }

    /// <summary>Contrôles communs à déplacer et copier ; renvoie le dossier source et le dossier cible.</summary>
    async Task<(string Source, string Target)> PlanAsync(string share, string path, string destParent, string newName, bool isFile, CancellationToken ct)
    {
        Validate(share, path);
        Validate(share, destParent);
        if (path.Length == 0) throw new OperationException(Msg.F("The share root has no parent folder."));
        if (AclRules.ValidateNewFolderName(newName) is { } e) throw new OperationException(e);
        if (!isFile && (destParent == path || destParent.StartsWith(path + "/", StringComparison.Ordinal)))
            throw new OperationException(Msg.F("A folder cannot be moved or copied into itself."));
        var root = await SharePathAsync(share, ct);
        var source = Dir(root, path, isFile);
        var destDir = Dir(root, destParent);
        var target = Path.Combine(destDir, newName);
        // Les clients Windows ignorent la casse : « Docs » et « docs » sont en collision, sauf s'il s'agit du même dossier qu'on renomme.
        if (new DirectoryInfo(destDir).EnumerateFileSystemInfos().Any(x => string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(x.FullName, source, StringComparison.Ordinal)))
            throw new OperationException(isFile ? Msg.F("An item named “{0}” already exists.", newName) : Msg.F("A folder named “{0}” already exists.", newName));
        return (source, target);
    }

    /// <summary>Déplace ou renomme un dossier ou un fichier.</summary>
    public async Task MoveFolderAsync(MoveFolderRequest r, CancellationToken ct = default)
    {
        var (source, target) = await PlanAsync(r.Share, r.Path, r.DestParent, r.NewName, r.IsFile, ct);
        try { if (r.IsFile) File.Move(source, target); else Directory.Move(source, target); } // rename(2) : l'ACL et les dates suivent
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new OperationException(Msg.F("{0} failed ({1}): {2}", "Moving the folder", 1, e.Message)); }
    }

    public async Task CopyFolderAsync(CopyFolderRequest r, CancellationToken ct = default)
    {
        var (source, target) = await PlanAsync(r.Share, r.Path, r.DestParent, r.NewName, r.IsFile, ct);
        try { if (r.IsFile) File.Copy(source, target); else CopyDir(new DirectoryInfo(source), target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new OperationException(Msg.F("{0} failed ({1}): {2}", "Copying the folder", 1, e.Message)); }
        // La copie appartient à l'agent : groupe et droits Unix de base comme partout, puis une ACL neutre
        // (sinon Samba en fabriquerait une depuis les droits Unix) avant que l'héritage de la destination ne s'y applique.
        Check(await run.RunAsync("chgrp", ["-R", UserService.Group, target], ct: ct), "Copying the folder");
        Check(await run.RunAsync("find", [target, "-type", "d", "-exec", "chmod", "2770", "{}", "+"], ct: ct), "Copying the folder");
        Check(await run.RunAsync("find", [target, "-type", "f", "-exec", "chmod", "0660", "{}", "+"], ct: ct), "Copying the folder");
        var rel = (r.DestParent.Length == 0 ? "" : r.DestParent + "/") + r.NewName;
        await ResetTreeAsync(r.Share, rel, ct);
    }

    static void CopyDir(DirectoryInfo from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in from.EnumerateFiles())
            if (f.LinkTarget is null) f.CopyTo(Path.Combine(to, f.Name));
        foreach (var d in from.EnumerateDirectories())
            if (d.LinkTarget is null) CopyDir(d, Path.Combine(to, d.Name));
    }

    /// <summary>Supprime un dossier et tout son contenu (jamais la racine du partage ; les liens symboliques ne sont pas suivis).</summary>
    public async Task DeleteFolderAsync(string share, string path, bool isFile = false, CancellationToken ct = default)
    {
        Validate(share, path);
        if (path.Length == 0) throw new OperationException(Msg.F("The share root cannot be deleted."));
        var dir = Dir(await SharePathAsync(share, ct), path, isFile);
        try { if (isFile) File.Delete(dir); else Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new OperationException(Msg.F("{0} failed ({1}): {2}", "Deleting the folder", 1, e.Message)); }
    }

    /// <summary>Sous-dossiers directs d'un dossier du partage, pour l'arborescence de l'interface.</summary>
    public async Task<FolderListing> ListFoldersAsync(string share, string path, bool withFiles = false, CancellationToken ct = default)
    {
        Validate(share, path);
        var root = await SharePathAsync(share, ct);
        var dir = path.Length == 0 ? root : Path.Combine(root, path);
        if (!Directory.Exists(dir) || new DirectoryInfo(dir).LinkTarget is not null) throw new OperationException(Msg.F("Invalid folder path."));
        var entries = new DirectoryInfo(dir).EnumerateDirectories()
            .Where(d => d.LinkTarget is null && AclRules.ValidSegment(d.Name))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(d => new FolderEntry(d.Name, d.LastWriteTimeUtc)).ToList();
        var files = withFiles
            ? new DirectoryInfo(dir).EnumerateFiles().Where(f => f.LinkTarget is null && AclRules.ValidSegment(f.Name))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => new FileEntry(f.Name, f.Length, f.LastWriteTimeUtc)).ToList()
            : null;
        return new FolderListing(path, entries, files);
    }

    /// <summary>Crée un dossier par Samba (compte de service) : le serveur lui applique lui-même l'héritage du parent.</summary>
    public async Task CreateFolderAsync(CreateFolderRequest req, CancellationToken ct = default)
    {
        var parent = Validate(req.Share, req.Path);
        if (AclRules.ValidateNewFolderName(req.Name) is { } e) throw new OperationException(e);
        await EnsureServiceAccountAsync(ct);
        var target = (parent == "/" ? "" : parent) + "/" + req.Name;
        var r = await run.RunAsync("smbclient", [$"//127.0.0.1/{req.Share}", "-A", AuthFile, "-c", $"mkdir \"{target}\""], ct: ct);
        var text = (r.StdOut + r.StdErr).Trim();
        if (r.ExitCode != 0 || text.Contains("NT_STATUS_", StringComparison.Ordinal))
            throw new OperationException(text.Contains("COLLISION", StringComparison.Ordinal)
                ? Msg.F("A folder named “{0}” already exists.", req.Name)
                : Msg.F("{0} failed ({1}): {2}", "Creating the folder", r.ExitCode, text));
    }

    /// <summary>Dossier d'un partage tel que Samba le connaît (la configuration générée fait foi).</summary>
    async Task<string> SharePathAsync(string share, CancellationToken ct)
    {
        var r = await run.RunAsync("testparm", ["-s", "--section-name=" + share, "--parameter-name=path"], ct: ct);
        var path = r.StdOut.Trim();
        if (r.ExitCode != 0 || !Path.IsPathRooted(path) || !Directory.Exists(path))
            throw new OperationException(Msg.F("Share “{0}” does not exist.", share));
        return path;
    }

    Task<CommandResult> Smbcacls(string share, string smbPath, IReadOnlyList<string> extra, CancellationToken ct) =>
        run.RunAsync("smbcacls", [$"//127.0.0.1/{share}", smbPath, "-A", AuthFile, .. extra], ct: ct);

    static void Check(CommandResult r, string what)
    {
        // Une restauration par lot affiche « Failed processing N files » ; les avertissements RPC ordinaires sont ignorés.
        var text = (r.StdOut + r.StdErr).Trim();
        if (r.ExitCode == 0 && !text.Contains("error: ", StringComparison.Ordinal)
            && !System.Text.RegularExpressions.Regex.IsMatch(text, @"Failed processing [1-9]")) return;
        throw new OperationException(text.Length > 0 ? Msg.F("{0} failed ({1}): {2}", what, r.ExitCode, text) : Msg.F("{0} failed ({1})", what, r.ExitCode));
    }
}
