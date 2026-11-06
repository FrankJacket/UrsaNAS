using Dapper;
using Nas.Contracts;
using Nas.Web.Alerting;

namespace Nas.Web.Data;

public class AuditRow
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Detail { get; set; }
}

public class AlertRow
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Detail { get; set; }
    public DateTime RaisedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public class AdminRow
{
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Stamp { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

class ShareRow
{
    public string Name { get; set; } = "";
    public string Comment { get; set; } = "";
    public bool ReadOnly { get; set; }
    public string ValidUsers { get; set; } = "";
    public string? Dataset { get; set; }
    public bool Acl { get; set; }
    public string? Nfs { get; set; }
    public string? Ftp { get; set; }

    public ShareDef ToDef() => new(Name, Comment, ReadOnly, ValidUsers.Split(' ', StringSplitOptions.RemoveEmptyEntries), Dataset, Acl,
        Nfs is null ? null : System.Text.Json.JsonSerializer.Deserialize<NfsExport>(Nfs),
        Ftp is null ? null : System.Text.Json.JsonSerializer.Deserialize<FtpAccess>(Ftp));
}

public class Repo(Db db)
{
    // ---- Journal : trace de chaque opération faite par l'interface
    public async Task AuditAsync(string actor, string action, string? detail = null)
    {
        using var c = db.Open();
        await c.ExecuteAsync(
            "INSERT INTO AuditLog(At,Actor,Action,Detail) VALUES(@at,@actor,@action,@detail)",
            new { at = DateTime.UtcNow, actor, action, detail });
    }

    public async Task<List<AuditRow>> GetAuditAsync(int limit = 200)
    {
        using var c = db.Open();
        return (await c.QueryAsync<AuditRow>(
            "SELECT Id,At,Actor,Action,Detail FROM AuditLog ORDER BY Id DESC LIMIT @limit", new { limit })).ToList();
    }

    // ---- Partages : définition voulue ; l'agent en fabrique la configuration Samba
    public async Task<List<ShareDef>> GetSharesAsync()
    {
        using var c = db.Open();
        return (await c.QueryAsync<ShareRow>("SELECT Name,Comment,ReadOnly,ValidUsers,Dataset,Acl,Nfs,Ftp FROM Shares ORDER BY Name"))
            .Select(r => r.ToDef()).ToList();
    }

    public async Task UpsertShareAsync(ShareDef s)
    {
        using var c = db.Open();
        await c.ExecuteAsync("""
            INSERT INTO Shares(Name,Comment,ReadOnly,ValidUsers,Dataset,Acl,Nfs,Ftp) VALUES(@Name,@Comment,@ReadOnly,@Users,@Dataset,@Acl,@Nfs,@Ftp)
            ON CONFLICT(Name) DO UPDATE SET Comment=@Comment, ReadOnly=@ReadOnly, ValidUsers=@Users, Dataset=@Dataset, Acl=@Acl, Nfs=@Nfs, Ftp=@Ftp
            """, new { s.Name, s.Comment, s.ReadOnly, Users = string.Join(' ', s.ValidUsers), s.Dataset, s.Acl,
                Nfs = s.Nfs is null ? null : System.Text.Json.JsonSerializer.Serialize(s.Nfs), Ftp = s.Ftp is null ? null : System.Text.Json.JsonSerializer.Serialize(s.Ftp) });
    }

    public async Task DeleteShareAsync(string name)
    {
        using var c = db.Open();
        await c.ExecuteAsync("DELETE FROM Shares WHERE Name=@name; DELETE FROM FolderAcls WHERE Share=@name", new { name });
    }

    // ---- Permissions avancées : les règles voulues de chaque dossier (l'agent les écrit dans les ACL de Samba)
    static readonly System.Text.Json.JsonSerializerOptions Json = new();

    public async Task<Dictionary<string, IReadOnlyList<AclRule>>> GetFolderAclsAsync(string share) =>
        (await GetFolderStatesAsync(share)).ToDictionary(p => p.Key, p => p.Value.Rules, StringComparer.Ordinal);

    public async Task<Dictionary<string, FolderState>> GetFolderStatesAsync(string share)
    {
        using var c = db.Open();
        var rows = await c.QueryAsync<(string Path, string Rules, int Inherit)>("SELECT Path,Rules,Inherit FROM FolderAcls WHERE Share=@share", new { share });
        return rows.ToDictionary(r => r.Path,
            r => new FolderState(System.Text.Json.JsonSerializer.Deserialize<List<AclRule>>(r.Rules, Json) ?? [], r.Inherit != 0), StringComparer.Ordinal);
    }

    public Task SetFolderAclAsync(string share, string path, IReadOnlyList<AclRule> rules) => SetFolderStateAsync(share, path, new FolderState(rules));

    /// <summary>Un dossier sans règle propre qui hérite n'a rien en base : la ligne est supprimée.</summary>
    public async Task SetFolderStateAsync(string share, string path, FolderState state)
    {
        using var c = db.Open();
        if (state.Rules.Count == 0 && state.Inherit)
            await c.ExecuteAsync("DELETE FROM FolderAcls WHERE Share=@share AND Path=@path", new { share, path });
        else
            await c.ExecuteAsync("""
                INSERT INTO FolderAcls(Share,Path,Rules,Inherit) VALUES(@share,@path,@json,@inherit)
                ON CONFLICT(Share,Path) DO UPDATE SET Rules=@json, Inherit=@inherit
                """, new { share, path, json = System.Text.Json.JsonSerializer.Serialize(state.Rules, Json), inherit = state.Inherit ? 1 : 0 });
    }

    // Un dossier et tout ce qu'il contient : le chemin lui-même, ou un chemin qui commence par « chemin/ » (pas de LIKE : _ et % sont des noms valides).
    const string InTree = "(Path=@path OR substr(Path,1,length(@path)+1)=@path||'/')";

    /// <summary>Un dossier déplacé ou renommé garde ses règles : les lignes du dossier et de ses sous-dossiers changent de chemin.</summary>
    public async Task RenameFolderStatesAsync(string share, string oldPath, string newPath)
    {
        using var c = db.Open();
        await c.ExecuteAsync($"DELETE FROM FolderAcls WHERE Share=@share AND {InTree}", new { share, path = newPath }); // restes d'un ancien dossier du même nom
        await c.ExecuteAsync($"UPDATE FolderAcls SET Path=@newPath||substr(Path,length(@path)+1) WHERE Share=@share AND {InTree}",
            new { share, path = oldPath, newPath });
    }

    /// <summary>Une copie reçoit les règles (et les ruptures d'héritage) du dossier d'origine et de ses sous-dossiers.</summary>
    public async Task CopyFolderStatesAsync(string share, string oldPath, string newPath)
    {
        using var c = db.Open();
        await c.ExecuteAsync($"DELETE FROM FolderAcls WHERE Share=@share AND {InTree}", new { share, path = newPath });
        await c.ExecuteAsync($"""
            INSERT INTO FolderAcls(Share,Path,Rules,Inherit)
            SELECT Share,@newPath||substr(Path,length(@path)+1),Rules,Inherit FROM FolderAcls WHERE Share=@share AND {InTree}
            """, new { share, path = oldPath, newPath });
    }

    public async Task DeleteFolderStatesAsync(string share, string path)
    {
        using var c = db.Open();
        await c.ExecuteAsync($"DELETE FROM FolderAcls WHERE Share=@share AND {InTree}", new { share, path });
    }

    // ---- Comptes qui peuvent se connecter à l'interface
    public async Task<int> CountAdminsAsync()
    {
        using var c = db.Open();
        return await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AdminUsers");
    }

    public async Task<AdminRow?> GetAdminAsync(string name)
    {
        using var c = db.Open();
        return await c.QuerySingleOrDefaultAsync<AdminRow>(
            "SELECT Name,PasswordHash,Stamp,CreatedAt FROM AdminUsers WHERE Name=@name", new { name });
    }

    public async Task<List<AdminRow>> ListAdminsAsync()
    {
        using var c = db.Open();
        return (await c.QueryAsync<AdminRow>("SELECT Name,PasswordHash,Stamp,CreatedAt FROM AdminUsers ORDER BY Name")).ToList();
    }

    public async Task InsertAdminAsync(string name, string hash, string stamp)
    {
        using var c = db.Open();
        await c.ExecuteAsync("INSERT INTO AdminUsers(Name,PasswordHash,Stamp,CreatedAt) VALUES(@name,@hash,@stamp,@at)",
            new { name, hash, stamp, at = DateTime.UtcNow });
    }

    public async Task UpdateAdminPasswordAsync(string name, string hash, string stamp)
    {
        using var c = db.Open();
        await c.ExecuteAsync("UPDATE AdminUsers SET PasswordHash=@hash, Stamp=@stamp WHERE Name=@name", new { name, hash, stamp });
    }

    public async Task DeleteAdminAsync(string name)
    {
        using var c = db.Open();
        await c.ExecuteAsync("DELETE FROM AdminUsers WHERE Name=@name", new { name });
    }

    // ---- Alertes
    public async Task<List<AlertRow>> GetActiveAlertsAsync()
    {
        using var c = db.Open();
        return (await c.QueryAsync<AlertRow>(
            "SELECT Id,Code,Severity,Title,Detail,RaisedAt,ResolvedAt FROM Alerts WHERE ResolvedAt IS NULL ORDER BY Id")).ToList();
    }

    public async Task<List<AlertRow>> GetAlertHistoryAsync(int limit = 100)
    {
        using var c = db.Open();
        return (await c.QueryAsync<AlertRow>(
            "SELECT Id,Code,Severity,Title,Detail,RaisedAt,ResolvedAt FROM Alerts ORDER BY Id DESC LIMIT @limit", new { limit })).ToList();
    }

    public async Task RaiseAlertAsync(Candidate a)
    {
        using var c = db.Open();
        await c.ExecuteAsync("INSERT INTO Alerts(Code,Severity,Title,Detail,RaisedAt) VALUES(@Code,@Severity,@Title,@Detail,@at)",
            new { a.Code, a.Severity, a.Title, a.Detail, at = DateTime.UtcNow });
    }

    public async Task ResolveAlertAsync(long id)
    {
        using var c = db.Open();
        await c.ExecuteAsync("UPDATE Alerts SET ResolvedAt=@at WHERE Id=@id AND ResolvedAt IS NULL", new { id, at = DateTime.UtcNow });
    }

    // ---- Réglages (clé/valeur)
    public async Task<MailSettings> GetMailSettingsAsync()
    {
        using var c = db.Open();
        var v = (await c.QueryAsync<(string Name, string Value)>("SELECT Name,Value FROM Settings WHERE Name LIKE 'mail.%'"))
            .ToDictionary(x => x.Name, x => x.Value);
        string S(string k, string d) => v.GetValueOrDefault("mail." + k, d);
        var d = MailSettings.Default;
        return new MailSettings(S("enabled", "0") == "1", S("host", d.Host), int.TryParse(S("port", ""), out var p) ? p : d.Port,
            S("security", d.Security), S("user", ""), S("password", ""), S("from", ""), S("to", ""), Localization.Translator.Normalize(S("language", "en")));
    }

    /// <summary>Un mot de passe vide conserve celui qui est déjà enregistré.</summary>
    public async Task SaveMailSettingsAsync(MailSettings s)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var pairs = new Dictionary<string, string>
        {
            ["enabled"] = s.Enabled ? "1" : "0", ["host"] = s.Host, ["port"] = s.Port.ToString(), ["security"] = s.Security,
            ["user"] = s.User, ["from"] = s.From, ["to"] = s.To, ["language"] = Localization.Translator.Normalize(s.Language),
        };
        if (s.Password.Length > 0) pairs["password"] = s.Password;
        foreach (var (k, val) in pairs)
            await c.ExecuteAsync("INSERT INTO Settings(Name,Value) VALUES(@n,@v) ON CONFLICT(Name) DO UPDATE SET Value=@v",
                new { n = "mail." + k, v = val }, tx);
        tx.Commit();
    }

    public async Task<FtpSettings> GetFtpSettingsAsync()
    {
        using var c = db.Open();
        var v = (await c.QueryAsync<(string Name, string Value)>("SELECT Name,Value FROM Settings WHERE Name LIKE 'ftp.%'"))
            .ToDictionary(x => x.Name, x => x.Value);
        var d = new FtpSettings();
        return new FtpSettings(v.GetValueOrDefault("ftp.tls") == "1",
            int.TryParse(v.GetValueOrDefault("ftp.pasvmin"), out var min) ? min : d.PassiveMin,
            int.TryParse(v.GetValueOrDefault("ftp.pasvmax"), out var max) ? max : d.PassiveMax);
    }

    public async Task SaveFtpSettingsAsync(FtpSettings s)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var pairs = new Dictionary<string, string> { ["ftp.tls"] = s.Tls ? "1" : "0", ["ftp.pasvmin"] = s.PassiveMin.ToString(), ["ftp.pasvmax"] = s.PassiveMax.ToString() };
        foreach (var (k, val) in pairs)
            await c.ExecuteAsync("INSERT INTO Settings(Name,Value) VALUES(@n,@v) ON CONFLICT(Name) DO UPDATE SET Value=@v", new { n = k, v = val }, tx);
        tx.Commit();
    }
}
