using Dapper;
using Nas.Contracts;

namespace Nas.Web.Data;

public class AuditRow
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Detail { get; set; }
}

class ShareRow
{
    public string Name { get; set; } = "";
    public string Comment { get; set; } = "";
    public bool ReadOnly { get; set; }
    public string ValidUsers { get; set; } = "";

    public ShareDef ToDef() => new(Name, Comment, ReadOnly, ValidUsers.Split(' ', StringSplitOptions.RemoveEmptyEntries));
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
        return (await c.QueryAsync<ShareRow>("SELECT Name,Comment,ReadOnly,ValidUsers FROM Shares ORDER BY Name"))
            .Select(r => r.ToDef()).ToList();
    }

    public async Task UpsertShareAsync(ShareDef s)
    {
        using var c = db.Open();
        await c.ExecuteAsync("""
            INSERT INTO Shares(Name,Comment,ReadOnly,ValidUsers) VALUES(@Name,@Comment,@ReadOnly,@Users)
            ON CONFLICT(Name) DO UPDATE SET Comment=@Comment, ReadOnly=@ReadOnly, ValidUsers=@Users
            """, new { s.Name, s.Comment, s.ReadOnly, Users = string.Join(' ', s.ValidUsers) });
    }

    public async Task DeleteShareAsync(string name)
    {
        using var c = db.Open();
        await c.ExecuteAsync("DELETE FROM Shares WHERE Name=@name", new { name });
    }
}
