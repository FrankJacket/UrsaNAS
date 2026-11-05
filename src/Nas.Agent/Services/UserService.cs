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
        return names.Select(n => new NasUser(n)).ToList();
    }

    public async Task CreateAsync(CreateUserRequest req, CancellationToken ct = default)
    {
        if (Rules.ValidateUserName(req.Name) is { } e1) throw new OperationException(e1);
        if (Rules.ValidatePassword(req.Password) is { } e2) throw new OperationException(e2);

        if ((await run.RunAsync("getent", ["group", Group], ct: ct)).ExitCode != 0)
            Check(await run.RunAsync("groupadd", ["--system", Group], ct: ct), "Création du groupe");

        if ((await run.RunAsync("id", ["-u", req.Name], ct: ct)).ExitCode == 0)
        {
            var managed = (await ListAsync(ct)).Any(u => u.Name == req.Name);
            throw new OperationException(managed
                ? $"L'utilisateur « {req.Name} » existe déjà."
                : $"« {req.Name} » est un compte système existant : il ne peut pas être utilisé.");
        }

        Check(await run.RunAsync("useradd",
            ["--no-create-home", "--shell", "/usr/sbin/nologin", "--gid", Group, req.Name], ct: ct), "Création du compte");

        var smb = await run.RunAsync("smbpasswd", ["-a", "-s", req.Name], PasswordInput(req.Password), ct);
        if (smb.ExitCode != 0)
        {
            await run.RunAsync("userdel", [req.Name], ct: ct); // ne pas laisser un compte sans mot de passe Samba
            Check(smb, "Création du compte Samba");
        }
    }

    public async Task SetPasswordAsync(string name, string password, CancellationToken ct = default)
    {
        await RequireManagedAsync(name, ct);
        if (Rules.ValidatePassword(password) is { } e) throw new OperationException(e);
        Check(await run.RunAsync("smbpasswd", ["-s", name], PasswordInput(password), ct), "Changement du mot de passe");
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        await RequireManagedAsync(name, ct);
        await run.RunAsync("smbpasswd", ["-x", name], ct: ct); // peut échouer si le compte Samba n'existe plus : sans gravité
        Check(await run.RunAsync("userdel", [name], ct: ct), "Suppression du compte");
    }

    async Task RequireManagedAsync(string name, CancellationToken ct)
    {
        if (Rules.ValidateUserName(name) is { } e) throw new OperationException(e);
        if (!(await ListAsync(ct)).Any(u => u.Name == name))
            throw new OperationException($"L'utilisateur « {name} » n'existe pas.");
    }

    // smbpasswd -s lit le mot de passe puis sa confirmation sur l'entrée standard.
    static string PasswordInput(string password) => $"{password}\n{password}\n";

    static void Check(CommandResult r, string what)
    {
        if (r.ExitCode == 0) return;
        var detail = (string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr).Trim();
        throw new OperationException($"{what} a échoué ({r.ExitCode}){(detail.Length > 0 ? " : " + detail : "")}");
    }
}
