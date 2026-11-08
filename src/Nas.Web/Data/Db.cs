using Dapper;
using Microsoft.Data.Sqlite;

namespace Nas.Web.Data;

public class Db
{
    readonly string _cs;
    public string DataDir { get; }

    public Db(IConfiguration cfg)
    {
        DataDir = Path.GetFullPath(cfg["DataDir"] ?? "data");
        Directory.CreateDirectory(DataDir);
        _cs = $"Data Source={Path.Combine(DataDir, "ursanas.db")}";
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    /// <summary>Crée le schéma ; les évolutions ultérieures passent par <see cref="AddColumn"/> pour migrer les bases existantes.</summary>
    public void Init()
    {
        using var c = Open();
        c.Execute("PRAGMA journal_mode=WAL;");
        c.Execute("""
        CREATE TABLE IF NOT EXISTS AuditLog(Id INTEGER PRIMARY KEY AUTOINCREMENT, At TEXT NOT NULL, Actor TEXT NOT NULL, Action TEXT NOT NULL, Detail TEXT);
        CREATE INDEX IF NOT EXISTS IX_AuditLog_At ON AuditLog(At);
        CREATE TABLE IF NOT EXISTS Shares(Name TEXT PRIMARY KEY COLLATE NOCASE, Comment TEXT NOT NULL DEFAULT '', ReadOnly INTEGER NOT NULL DEFAULT 0, ValidUsers TEXT NOT NULL); -- ValidUsers : noms séparés par des espaces
        CREATE TABLE IF NOT EXISTS Alerts(Id INTEGER PRIMARY KEY AUTOINCREMENT, Code TEXT NOT NULL, Severity TEXT NOT NULL, Title TEXT NOT NULL, Detail TEXT, RaisedAt TEXT NOT NULL, ResolvedAt TEXT);
        CREATE INDEX IF NOT EXISTS IX_Alerts_Code ON Alerts(Code);
        CREATE TABLE IF NOT EXISTS AdminUsers(Name TEXT PRIMARY KEY COLLATE NOCASE, PasswordHash TEXT NOT NULL, Stamp TEXT NOT NULL, CreatedAt TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS Settings(Name TEXT PRIMARY KEY, Value TEXT NOT NULL);
        """);
        AddColumn(c, "Shares", "Dataset", "TEXT NULL"); // jeu de données ZFS ; NULL = dossier simple dans /srv/nas
        AddColumn(c, "Shares", "Acl", "INTEGER NOT NULL DEFAULT 0"); // 1 = permissions avancées (ACL NT)
        AddColumn(c, "Shares", "Nfs", "TEXT NULL"); // JSON de NfsExport ; NULL = pas exporté en NFS
        AddColumn(c, "Shares", "Disk", "TEXT NULL"); // dossier de montage d'un disque (sous /mnt) utilisé comme partage ; NULL = dossier simple ou jeu de données
        AddColumn(c, "Shares", "Ftp", "TEXT NULL"); // JSON de FtpAccess ; NULL = pas accessible en FTP
        c.Execute("CREATE TABLE IF NOT EXISTS FolderAcls(Share TEXT NOT NULL COLLATE NOCASE, Path TEXT NOT NULL, Rules TEXT NOT NULL, PRIMARY KEY(Share, Path)); -- Rules : JSON de AclRule[]");
        AddColumn(c, "FolderAcls", "Inherit", "INTEGER NOT NULL DEFAULT 1"); // 0 = héritage rompu
        c.Execute("""
        CREATE TABLE IF NOT EXISTS BackupJobs(Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Share TEXT NOT NULL COLLATE NOCASE, Includes TEXT NOT NULL DEFAULT '',
            TargetDir TEXT NOT NULL, TargetId TEXT NULL, DestFolder TEXT NOT NULL, Enabled INTEGER NOT NULL DEFAULT 1, ScheduleMode INTEGER NOT NULL DEFAULT 0,
            DailyTime TEXT NOT NULL DEFAULT '02:00', MirrorDeletes INTEGER NOT NULL DEFAULT 1, RetentionDays INTEGER NOT NULL DEFAULT 30, Verify INTEGER NOT NULL DEFAULT 0,
            WindowsDisk INTEGER NOT NULL DEFAULT 1, Excludes TEXT NOT NULL DEFAULT '', Snapshot INTEGER NOT NULL DEFAULT 0, CreatedUtc TEXT NOT NULL,
            EstimatedFiles INTEGER NOT NULL DEFAULT 0, EstimatedBytes INTEGER NOT NULL DEFAULT 0); -- Includes, Excludes : une entrée par ligne
        CREATE TABLE IF NOT EXISTS BackupRuns(Id INTEGER PRIMARY KEY AUTOINCREMENT, JobId INTEGER NOT NULL, JobName TEXT NOT NULL, StartedUtc TEXT NOT NULL, EndedUtc TEXT NULL,
            Status TEXT NOT NULL, Reason TEXT NOT NULL DEFAULT '', FilesSeen INTEGER NOT NULL DEFAULT 0, Copied INTEGER NOT NULL DEFAULT 0, BytesCopied INTEGER NOT NULL DEFAULT 0,
            Unchanged INTEGER NOT NULL DEFAULT 0, Deleted INTEGER NOT NULL DEFAULT 0, Errors INTEGER NOT NULL DEFAULT 0, Message TEXT NULL, Details TEXT NULL);
        CREATE INDEX IF NOT EXISTS IX_BackupRuns_Job ON BackupRuns(JobId, Id);
        """);
    }

    static void AddColumn(SqliteConnection c, string table, string column, string ddl)
    {
        if (!c.Query<string>($"SELECT name FROM pragma_table_info('{table}')").Contains(column))
            c.Execute($"ALTER TABLE {table} ADD COLUMN {column} {ddl}");
    }
}
