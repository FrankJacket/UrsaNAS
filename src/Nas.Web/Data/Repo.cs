using Dapper;

namespace Nas.Web.Data;

public class AuditRow
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Detail { get; set; }
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
}
