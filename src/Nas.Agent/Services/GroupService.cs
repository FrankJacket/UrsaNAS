using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>
/// Groupes du NAS : de vrais groupes Linux dont le nom commence par <c>nasg-</c>. Seuls ces groupes sont jamais modifiés.
/// Ils servent de sujet aux règles de permission (SID Unix <c>S-1-22-2-&lt;gid&gt;</c> côté Samba). Un changement de membres
/// ne touche que les NOUVELLES connexions : une session SMB ouverte garde les groupes qu'elle avait à la connexion.
/// </summary>
public sealed class GroupService(ICommandRunner run, UserService users)
{
    public static string SystemName(string name) => AclRules.GroupPrefix + name;

    public async Task<IReadOnlyList<NasGroup>> ListAsync(CancellationToken ct = default)
    {
        var r = await run.RunAsync("getent", ["group"], ct: ct);
        return ParseGroups(r.StdOut);
    }

    /// <summary>Lignes <c>nom:x:gid:membre1,membre2</c> ; ne garde que les groupes d'UrsaNAS, sans le préfixe.</summary>
    public static IReadOnlyList<NasGroup> ParseGroups(string getentGroup) =>
        getentGroup.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(':'))
            .Where(f => f.Length >= 4 && f[0].StartsWith(AclRules.GroupPrefix, StringComparison.Ordinal))
            .Select(f => new NasGroup(f[0][AclRules.GroupPrefix.Length..],
                f[3].Split(',', StringSplitOptions.RemoveEmptyEntries).OrderBy(m => m, StringComparer.Ordinal).ToList()))
            .OrderBy(g => g.Name, StringComparer.Ordinal).ToList();

    /// <summary>Numéro de groupe (gid), ou <c>null</c> si le groupe n'existe pas.</summary>
    public async Task<int?> GidAsync(string name, CancellationToken ct = default)
    {
        var r = await run.RunAsync("getent", ["group", SystemName(name)], ct: ct);
        if (r.ExitCode != 0) return null;
        var f = r.StdOut.Trim().Split(':');
        return f.Length >= 3 && int.TryParse(f[2], out var gid) ? gid : null;
    }

    public async Task CreateAsync(CreateGroupRequest req, CancellationToken ct = default)
    {
        if (AclRules.ValidateGroupName(req.Name) is { } e) throw new OperationException(e);
        if (AclRules.IsFixedGroup(req.Name)) throw new OperationException(Msg.F("Group “{0}” already exists.", req.Name));
        await RequireMembersAsync(req.Members, ct);
        if (await GidAsync(req.Name, ct) is not null) throw new OperationException(Msg.F("Group “{0}” already exists.", req.Name));
        Check(await run.RunAsync("groupadd", ["--system", SystemName(req.Name)], ct: ct), "Creating the group");
        if (req.Members.Count > 0) await SetMembersAsync(req.Name, req.Members, ct);
    }

    public async Task SetMembersAsync(string name, IReadOnlyList<string> members, CancellationToken ct = default)
    {
        await RequireExistsAsync(name, ct);
        await RequireMembersAsync(members, ct);
        if (name == AclRules.AllUsersGroup) throw new OperationException(Msg.F("Everyone is always a member of this group."));
        if (name == AclRules.AdminsGroup && members.Count == 0) throw new OperationException(Msg.F("Cannot remove the last administrator."));
        // gpasswd -M remplace la liste entière ; une liste vide vide le groupe.
        Check(await run.RunAsync("gpasswd", ["-M", string.Join(',', members.Distinct().OrderBy(m => m, StringComparer.Ordinal)), SystemName(name)], ct: ct), "Changing the members");
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        if (AclRules.IsFixedGroup(name)) throw new OperationException(Msg.F("This group is part of UrsaNAS and cannot be deleted."));
        await RequireExistsAsync(name, ct);
        Check(await run.RunAsync("groupdel", [SystemName(name)], ct: ct), "Deleting the group");
    }

    async Task RequireExistsAsync(string name, CancellationToken ct)
    {
        if (AclRules.ValidateGroupName(name) is { } e) throw new OperationException(e);
        if (await GidAsync(name, ct) is null) throw new OperationException(Msg.F("Group “{0}” does not exist.", name));
    }

    async Task RequireMembersAsync(IReadOnlyList<string> members, CancellationToken ct)
    {
        var known = (await users.ListAsync(ct)).Select(u => u.Name).ToHashSet();
        foreach (var m in members)
            if (!known.Contains(m)) throw new OperationException(Msg.F("User “{0}” does not exist.", m));
    }

    static void Check(CommandResult r, string what)
    {
        if (r.ExitCode == 0) return;
        var detail = (string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr).Trim();
        throw new OperationException(detail.Length > 0 ? Msg.F("{0} failed ({1}): {2}", what, r.ExitCode, detail) : Msg.F("{0} failed ({1})", what, r.ExitCode));
    }
}
