using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>Erreur attendue (entrée refusée, commande échouée) : renvoyée telle quelle à l'interface avec un code 400.</summary>
public sealed class OperationException(string message) : Exception(message);

/// <summary>
/// Utilisateurs du NAS : un compte système sans shell ni dossier personnel, membre du groupe <c>nasusers</c>,
/// plus le compte Samba correspondant. Seuls les comptes de ce groupe sont jamais modifiés ou supprimés.
/// </summary>
public sealed class UserService(ICommandRunner run)
{
    public const string Group = "nasusers";

    public async Task<IReadOnlyList<NasUser>> ListAsync(CancellationToken ct = default)
    {
        var g = await run.RunAsync("getent", ["group", Group], ct: ct);
        if (g.ExitCode != 0) return [];

        // Format : nom:x:gid:membre1,membre2. Les comptes créés ici ont ce groupe comme groupe principal :
        // ils n'apparaissent pas dans la liste des membres, il faut aussi parcourir passwd.
        var f = g.StdOut.Trim().Split(':');
        var names = new SortedSet<string>(StringComparer.Ordinal);
        if (f.Length >= 4)
            foreach (var m in f[3].Split(',', StringSplitOptions.RemoveEmptyEntries)) names.Add(m);

        var gid = f.Length >= 3 ? f[2] : "";
        var p = await run.RunAsync("getent", ["passwd"], ct: ct);
        foreach (var line in p.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var pf = line.Split(':');
            if (pf.Length >= 4 && pf[3] == gid) names.Add(pf[0]);
        }
        var admins = await MembersAsync(AclRules.AdminsGroup, ct) ?? [];
        return names.Select(n => new NasUser(n, admins.Contains(n))).ToList();
    }

    /// <summary>Crée le groupe des comptes et des partages s'il n'existe pas encore (il n'existe pas avant le premier utilisateur sur une installation neuve).</summary>
    public async Task EnsureGroupAsync(CancellationToken ct = default)
    {
        if ((await run.RunAsync("getent", ["group", Group], ct: ct)).ExitCode != 0)
            Check(await run.RunAsync("groupadd", ["--system", Group], ct: ct), "Creating the group");
    }

    static string SystemGroup(string name) => AclRules.GroupPrefix + name;

    /// <summary>Membres d'un groupe fixe (« admins », « allusers »), ou <c>null</c> si le groupe n'existe pas encore.</summary>
    public async Task<List<string>?> MembersAsync(string name, CancellationToken ct = default)
    {
        var r = await run.RunAsync("getent", ["group", SystemGroup(name)], ct: ct);
        if (r.ExitCode != 0) return null;
        var f = r.StdOut.Trim().Split(':');
        return f.Length >= 4 ? f[3].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() : [];
    }

    async Task SetMembersAsync(string name, IEnumerable<string> members, CancellationToken ct) =>
        Check(await run.RunAsync("gpasswd", ["-M", string.Join(',', members.Distinct().Order(StringComparer.Ordinal)), SystemGroup(name)], ct: ct), "Changing the members");

    async Task AddMemberAsync(string group, string user, CancellationToken ct)
    {
        var members = await MembersAsync(group, ct) ?? [];
        if (!members.Contains(user)) await SetMembersAsync(group, [.. members, user], ct);
    }

    /// <summary>
    /// Crée les groupes fixes s'ils manquent (« admins » et « allusers ») et range dans « allusers » les comptes qui n'y sont pas encore
    /// (utilisateurs créés avant l'existence de ce groupe). Appelé au démarrage de l'agent et avant d'appliquer les partages.
    /// </summary>
    public async Task EnsureSystemGroupsAsync(CancellationToken ct = default)
    {
        await EnsureGroupAsync(ct);
        foreach (var g in new[] { AclRules.AdminsGroup, AclRules.AllUsersGroup })
            if (await MembersAsync(g, ct) is null)
                Check(await run.RunAsync("groupadd", ["--system", SystemGroup(g)], ct: ct), "Creating the group");

        var everyone = (await ListAsync(ct)).Select(u => u.Name).ToList();
        var inGroup = await MembersAsync(AclRules.AllUsersGroup, ct) ?? [];
        if (everyone.Any(u => !inGroup.Contains(u))) await SetMembersAsync(AclRules.AllUsersGroup, [.. inGroup, .. everyone], ct);
    }

    /// <summary>Donne ou retire le rôle d'administrateur (appartenance au groupe « admins »). Le dernier administrateur ne peut pas le perdre.</summary>
    public async Task SetAdminAsync(string name, bool admin, CancellationToken ct = default)
    {
        await RequireManagedAsync(name, ct);
        await EnsureSystemGroupsAsync(ct);
        var members = await MembersAsync(AclRules.AdminsGroup, ct) ?? [];
        if (admin) { await AddMemberAsync(AclRules.AdminsGroup, name, ct); return; }
        if (members.Contains(name) && members.Count <= 1) throw new OperationException(Msg.F("Cannot remove the last administrator."));
        await SetMembersAsync(AclRules.AdminsGroup, members.Where(m => m != name), ct);
    }

    /// <summary>Empreinte NT du compte Samba (null : compte inconnu ou sans mot de passe).</summary>
    async Task<string?> NtHashOfAsync(string name, CancellationToken ct)
    {
        var r = await run.RunAsync("pdbedit", ["-L", "-w", "-u", name], ct: ct);
        return r.ExitCode == 0 ? NtHash.FromPdbedit(r.StdOut, name) : null;
    }

    /// <summary>Le mot de passe est celui du compte Samba du NAS (la même base que celle des partages : une seule source de vérité).</summary>
    public async Task<UserSession> VerifyAsync(VerifyUserRequest req, CancellationToken ct = default)
    {
        if (Rules.ValidateUserName(req.Name) is not null) return new UserSession(false, false, "");
        var user = (await ListAsync(ct)).FirstOrDefault(u => u.Name == req.Name);
        var hash = user is null ? null : await NtHashOfAsync(req.Name, ct);
        if (user is null || hash is null || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(hash), System.Text.Encoding.ASCII.GetBytes(NtHash.Of(req.Password))))
            return new UserSession(false, false, "");
        return new UserSession(true, user.Admin, StampOf(hash));
    }

    /// <summary>Le compte existe-t-il encore, est-il administrateur, et son mot de passe a-t-il changé (le tampon change) ?</summary>
    public async Task<UserSession> SessionAsync(string name, CancellationToken ct = default)
    {
        if (Rules.ValidateUserName(name) is not null) return new UserSession(false, false, "");
        var user = (await ListAsync(ct)).FirstOrDefault(u => u.Name == name);
        var hash = user is null ? null : await NtHashOfAsync(name, ct);
        return user is null || hash is null ? new UserSession(false, false, "") : new UserSession(true, user.Admin, StampOf(hash));
    }

    // Le tampon n'expose pas l'empreinte : on n'en garde qu'un résumé.
    static string StampOf(string hash) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes("ursanas-stamp:" + hash)))[..24];

    public async Task CreateAsync(CreateUserRequest req, CancellationToken ct = default)
    {
        if (Rules.ValidateUserName(req.Name) is { } e1) throw new OperationException(e1);
        if (Rules.ValidatePassword(req.Password) is { } e2) throw new OperationException(e2);

        await EnsureSystemGroupsAsync(ct);

        if ((await run.RunAsync("id", ["-u", req.Name], ct: ct)).ExitCode == 0)
        {
            var managed = (await ListAsync(ct)).Any(u => u.Name == req.Name);
            throw new OperationException(managed
                ? Msg.F("User “{0}” already exists.", req.Name)
                : Msg.F("“{0}” is an existing system account: it cannot be used.", req.Name));
        }

        Check(await run.RunAsync("useradd",
            ["--no-create-home", "--shell", "/usr/sbin/nologin", "--gid", Group, req.Name], ct: ct), "Creating the account");

        var smb = await run.RunAsync("smbpasswd", ["-a", "-s", req.Name], PasswordInput(req.Password), ct);
        if (smb.ExitCode != 0)
        {
            await run.RunAsync("userdel", [req.Name], ct: ct); // ne pas laisser un compte sans mot de passe Samba
            Check(smb, "Creating the Samba account");
        }

        // Le même mot de passe sert au compte système : FTP s'authentifie par PAM, pas par Samba.
        var unix = await run.RunAsync("chpasswd", [], UnixPasswordInput(req.Name, req.Password), ct);
        if (unix.ExitCode != 0)
        {
            await run.RunAsync("smbpasswd", ["-x", req.Name], ct: ct);
            await run.RunAsync("userdel", [req.Name], ct: ct);
            Check(unix, "Setting the account password");
        }

        // Tout nouvel utilisateur est membre de « allusers » ; le rôle d'administrateur est le groupe « admins ».
        await AddMemberAsync(AclRules.AllUsersGroup, req.Name, ct);
        if (req.Admin) await AddMemberAsync(AclRules.AdminsGroup, req.Name, ct);
    }

    public async Task SetPasswordAsync(string name, string password, CancellationToken ct = default)
    {
        await RequireManagedAsync(name, ct);
        if (Rules.ValidatePassword(password) is { } e) throw new OperationException(e);
        Check(await run.RunAsync("smbpasswd", ["-s", name], PasswordInput(password), ct), "Changing the password");
        Check(await run.RunAsync("chpasswd", [], UnixPasswordInput(name, password), ct), "Changing the password");
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        await RequireManagedAsync(name, ct);
        var admins = await MembersAsync(AclRules.AdminsGroup, ct) ?? [];
        if (admins.Contains(name) && admins.Count <= 1) throw new OperationException(Msg.F("Cannot delete the last administrator."));
        await run.RunAsync("smbpasswd", ["-x", name], ct: ct); // peut échouer si le compte Samba n'existe plus : sans gravité
        Check(await run.RunAsync("userdel", [name], ct: ct), "Deleting the account");
    }

    async Task RequireManagedAsync(string name, CancellationToken ct)
    {
        if (Rules.ValidateUserName(name) is { } e) throw new OperationException(e);
        if (!(await ListAsync(ct)).Any(u => u.Name == name))
            throw new OperationException(Msg.F("User “{0}” does not exist.", name));
    }

    // smbpasswd -s lit le mot de passe puis sa confirmation sur l'entrée standard.
    static string PasswordInput(string password) => $"{password}\n{password}\n";

    // chpasswd lit « nom:mot de passe » sur l'entrée standard (le nom ne contient jamais de « : », le mot de passe peut en contenir).
    static string UnixPasswordInput(string name, string password) => $"{name}:{password}\n";

    static void Check(CommandResult r, string what)
    {
        if (r.ExitCode == 0) return;
        var detail = (string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr).Trim();
        throw new OperationException(detail.Length > 0
            ? Msg.F("{0} failed ({1}): {2}", what, r.ExitCode, detail)
            : Msg.F("{0} failed ({1})", what, r.ExitCode));
    }
}
