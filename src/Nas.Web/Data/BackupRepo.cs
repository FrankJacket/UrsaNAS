using Dapper;
using Nas.Contracts;

namespace Nas.Web.Data;

public static class BackupSchedule
{
    public const int Manual = 0, Daily = 1, OnPlug = 2;
}

/// <summary>
/// Un travail de sauvegarde : des partages (ou des dossiers et fichiers de ces partages) copiés vers un dossier d'un disque externe.
/// Deux formes : ancienne (<see cref="Share"/> renseigné, un seul partage copié directement dans le dossier du disque, chemins relatifs au partage)
/// et courante (<see cref="Share"/> vide, un sous-dossier par partage, chemins de la forme <c>partage</c> ou <c>partage/dossier/fichier</c>).
/// </summary>
public sealed class BackupJobRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Ancienne forme : le partage unique du travail. Vide pour la forme courante.</summary>
    public string Share { get; set; } = "";
    /// <summary>Ce qui est copié, un chemin par ligne (voir la description de la classe) ; vide en ancienne forme = tout le partage.</summary>
    public string Includes { get; set; } = "";
    public string TargetDir { get; set; } = "";
    public string? TargetId { get; set; }
    public string DestFolder { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int ScheduleMode { get; set; } = BackupSchedule.Manual;
    public string DailyTime { get; set; } = "02:00";
    public bool MirrorDeletes { get; set; } = true;
    public int RetentionDays { get; set; } = 30;
    public bool Verify { get; set; }
    public bool WindowsDisk { get; set; } = true;
    public string Excludes { get; set; } = string.Join('\n', BackupRules.DefaultExcludes);
    public bool Snapshot { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Fichiers et octets vus par la dernière copie complète : base de la barre de progression.</summary>
    public long EstimatedFiles { get; set; }
    public long EstimatedBytes { get; set; }

    public string[] IncludeList() => Lines(Includes);

    /// <summary>Forme courante : un sous-dossier par partage sur le disque.</summary>
    public bool SubFolders => Share.Length == 0;

    /// <summary>Ce que le travail copie, sous la forme (partage, chemin relatif ; vide = tout le partage), quelle que soit la forme.</summary>
    public List<(string Share, string Path)> Selection()
    {
        var list = IncludeList();
        if (!SubFolders) return list.Length == 0 ? [(Share, "")] : list.Select(p => (Share, p)).ToList();
        return list.Select(p => { var i = p.IndexOf('/'); return i < 0 ? (p, "") : (p[..i], p[(i + 1)..]); }).ToList();
    }

    /// <summary>Remplit <see cref="Share"/> et <see cref="Includes"/> depuis une sélection. Un travail ancien qui ne copie toujours qu'un partage garde son rangement.</summary>
    public void SetSelection(IEnumerable<(string Share, string Path)> selection, bool keepLegacy)
    {
        var items = selection.ToList();
        var shares = items.Select(i => i.Share).Distinct(StringComparer.Ordinal).ToList();
        if (keepLegacy && shares.Count == 1)
        {
            Share = shares[0];
            Includes = items.Any(i => i.Path.Length == 0) ? "" : string.Join('\n', items.Select(i => i.Path).OrderBy(p => p, StringComparer.Ordinal));
        }
        else
        {
            Share = "";
            Includes = string.Join('\n', items.Select(i => i.Path.Length == 0 ? i.Share : i.Share + "/" + i.Path).OrderBy(p => p, StringComparer.Ordinal));
        }
    }
    public string[] ExcludeList() => Lines(Excludes);
    static string[] Lines(string s) => s.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed class BackupRunRow
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public string JobName { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    /// <summary>Running, Success, CompletedWithErrors, Failed, Cancelled ou Interrupted.</summary>
    public string Status { get; set; } = "Running";
    public string Reason { get; set; } = "";
    public long FilesSeen { get; set; }
    public long Copied { get; set; }
    public long BytesCopied { get; set; }
    public long Unchanged { get; set; }
    public long Deleted { get; set; }
    public long Errors { get; set; }
    public string? Message { get; set; }
    public string? Details { get; set; }
}

/// <summary>Résumé d'un travail pour les alertes : sa dernière copie et sa dernière réussite.</summary>
public sealed record BackupHealth(int JobId, string Name, bool Enabled, int ScheduleMode, DateTime CreatedUtc, string? LastStatus, DateTime? LastSuccessUtc);

public class BackupRepo(Db db)
{
    const string JobColumns = "Id,Name,Share,Includes,TargetDir,TargetId,DestFolder,Enabled,ScheduleMode,DailyTime,MirrorDeletes,RetentionDays,Verify,WindowsDisk,Excludes,Snapshot,CreatedUtc,EstimatedFiles,EstimatedBytes";

    public async Task<List<BackupJobRow>> GetJobsAsync()
    {
        using var c = db.Open();
        return (await c.QueryAsync<BackupJobRow>($"SELECT {JobColumns} FROM BackupJobs ORDER BY Name COLLATE NOCASE")).ToList();
    }

    public async Task<BackupJobRow?> GetJobAsync(int id)
    {
        using var c = db.Open();
        return await c.QuerySingleOrDefaultAsync<BackupJobRow>($"SELECT {JobColumns} FROM BackupJobs WHERE Id=@id", new { id });
    }

    /// <summary>Crée (Id = 0) ou met à jour un travail ; renvoie son identifiant. L'identifiant du disque et les estimations ne changent pas ici.</summary>
    public async Task<int> SaveJobAsync(BackupJobRow j)
    {
        using var c = db.Open();
        if (j.Id == 0)
            return await c.ExecuteScalarAsync<int>("""
                INSERT INTO BackupJobs(Name,Share,Includes,TargetDir,TargetId,DestFolder,Enabled,ScheduleMode,DailyTime,MirrorDeletes,RetentionDays,Verify,WindowsDisk,Excludes,Snapshot,CreatedUtc)
                VALUES(@Name,@Share,@Includes,@TargetDir,@TargetId,@DestFolder,@Enabled,@ScheduleMode,@DailyTime,@MirrorDeletes,@RetentionDays,@Verify,@WindowsDisk,@Excludes,@Snapshot,@CreatedUtc);
                SELECT last_insert_rowid();
                """, j);
        await c.ExecuteAsync("""
            UPDATE BackupJobs SET Name=@Name,Share=@Share,Includes=@Includes,TargetDir=@TargetDir,TargetId=@TargetId,DestFolder=@DestFolder,Enabled=@Enabled,ScheduleMode=@ScheduleMode,
              DailyTime=@DailyTime,MirrorDeletes=@MirrorDeletes,RetentionDays=@RetentionDays,Verify=@Verify,WindowsDisk=@WindowsDisk,Excludes=@Excludes,Snapshot=@Snapshot WHERE Id=@Id
            """, j);
        return j.Id;
    }

    public async Task DeleteJobAsync(int id)
    {
        using var c = db.Open();
        await c.ExecuteAsync("DELETE FROM BackupRuns WHERE JobId=@id; DELETE FROM BackupJobs WHERE Id=@id", new { id });
    }

    /// <summary>Un disque initialisé vaut pour tous les travaux qui l'utilisent et n'ont pas encore d'identifiant.</summary>
    public async Task AdoptDiskAsync(string targetDir, string targetId)
    {
        using var c = db.Open();
        await c.ExecuteAsync("UPDATE BackupJobs SET TargetId=@targetId WHERE TargetDir=@targetDir AND TargetId IS NULL", new { targetDir, targetId });
    }

    public async Task SetEstimateAsync(int id, long files, long bytes)
    {
        using var c = db.Open();
        await c.ExecuteAsync("UPDATE BackupJobs SET EstimatedFiles=@files, EstimatedBytes=@bytes WHERE Id=@id", new { id, files, bytes });
    }

    // ---- Exécutions

    public async Task<int> AddRunAsync(BackupJobRow j, string reason)
    {
        using var c = db.Open();
        return await c.ExecuteScalarAsync<int>("""
            INSERT INTO BackupRuns(JobId,JobName,StartedUtc,Status,Reason) VALUES(@Id,@Name,@now,'Running',@reason);
            SELECT last_insert_rowid();
            """, new { j.Id, j.Name, now = DateTime.UtcNow, reason });
    }

    public async Task FinishRunAsync(BackupRunRow r)
    {
        using var c = db.Open();
        await c.ExecuteAsync("""
            UPDATE BackupRuns SET EndedUtc=@EndedUtc,Status=@Status,Message=@Message,Details=@Details,FilesSeen=@FilesSeen,Copied=@Copied,BytesCopied=@BytesCopied,
              Unchanged=@Unchanged,Deleted=@Deleted,Errors=@Errors WHERE Id=@Id
            """, r);
    }

    public async Task<BackupRunRow?> GetOpenRunAsync(int jobId)
    {
        using var c = db.Open();
        return await c.QueryFirstOrDefaultAsync<BackupRunRow>("SELECT * FROM BackupRuns WHERE JobId=@jobId AND Status='Running' ORDER BY Id DESC LIMIT 1", new { jobId });
    }

    public async Task<List<BackupRunRow>> GetOpenRunsAsync()
    {
        using var c = db.Open();
        return (await c.QueryAsync<BackupRunRow>("SELECT * FROM BackupRuns WHERE Status='Running'")).ToList();
    }

    public async Task<List<BackupRunRow>> GetRunsAsync(int? jobId = null, int limit = 100)
    {
        using var c = db.Open();
        return (await c.QueryAsync<BackupRunRow>(
            "SELECT * FROM BackupRuns WHERE (@jobId IS NULL OR JobId=@jobId) ORDER BY Id DESC LIMIT @limit", new { jobId, limit })).ToList();
    }

    public async Task<DateTime?> LastStartedUtcAsync(int jobId)
    {
        using var c = db.Open();
        return await c.ExecuteScalarAsync<DateTime?>("SELECT MAX(StartedUtc) FROM BackupRuns WHERE JobId=@jobId", new { jobId });
    }

    public async Task<BackupRunRow?> LastFinishedRunAsync(int jobId)
    {
        using var c = db.Open();
        return await c.QueryFirstOrDefaultAsync<BackupRunRow>("SELECT * FROM BackupRuns WHERE JobId=@jobId AND Status<>'Running' ORDER BY Id DESC LIMIT 1", new { jobId });
    }

    /// <summary>Pour les alertes : la dernière copie terminée (hors annulation) et la dernière réussite de chaque travail.</summary>
    public async Task<List<BackupHealth>> GetHealthAsync()
    {
        using var c = db.Open();
        var rows = await c.QueryAsync<(int Id, string Name, bool Enabled, int Mode, DateTime Created, string? Last, DateTime? Success)>("""
            SELECT j.Id, j.Name, j.Enabled, j.ScheduleMode, j.CreatedUtc,
              (SELECT Status FROM BackupRuns r WHERE r.JobId=j.Id AND r.Status NOT IN ('Running','Cancelled') ORDER BY r.Id DESC LIMIT 1),
              (SELECT MAX(EndedUtc) FROM BackupRuns r WHERE r.JobId=j.Id AND r.Status IN ('Success','CompletedWithErrors'))
            FROM BackupJobs j
            """);
        return rows.Select(r => new BackupHealth(r.Id, r.Name, r.Enabled, r.Mode, r.Created, r.Last, r.Success)).ToList();
    }

    /// <summary>Garde les 500 dernières exécutions de chaque travail.</summary>
    public async Task PruneRunsAsync(int jobId, int keep = 500)
    {
        using var c = db.Open();
        await c.ExecuteAsync("DELETE FROM BackupRuns WHERE JobId=@jobId AND Id NOT IN (SELECT Id FROM BackupRuns WHERE JobId=@jobId ORDER BY Id DESC LIMIT @keep)", new { jobId, keep });
    }
}
