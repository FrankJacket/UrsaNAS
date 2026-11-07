using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Nas.Contracts;
using Ursacopy.Core;

namespace Nas.Agent.Services;

/// <summary>Une ligne de /proc/self/mountinfo : où, quel système de fichiers, quel périphérique.</summary>
public sealed record MountEntry(string MountPoint, string? FileSystem, string? Device);

public static partial class MountTable
{
    /// <summary>Lit /proc/self/mountinfo (espaces et antislash écrits en octal dans les chemins).</summary>
    public static IReadOnlyList<MountEntry> Parse(string text)
    {
        var list = new List<MountEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(' ');
            var dash = Array.IndexOf(f, "-");
            if (f.Length < 5 || dash < 0) continue;
            list.Add(new MountEntry(Unescape(f[4]), dash + 1 < f.Length ? f[dash + 1] : null, dash + 2 < f.Length ? Unescape(f[dash + 2]) : null));
        }
        return list;
    }

    static string Unescape(string s) => s.Contains('\\')
        ? Octal().Replace(s, m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString())
        : s;

    [GeneratedRegex(@"\\([0-7]{3})")]
    private static partial Regex Octal();
}

/// <summary>
/// Sauvegardes : copie un partage vers un disque externe monté à la main, avec le moteur d'Ursacopy. Une copie à la fois (un seul
/// disque USB : deux copies parallèles ne feraient que le faire chercher). La source est toujours un partage connu de Samba ; la cible
/// doit être un point de montage sous une racine autorisée (/mnt, /media…) et porter le marqueur du travail.
/// </summary>
public sealed class BackupService
{
    readonly ICommandRunner _run;
    readonly AclService _acl;
    readonly IConfiguration _cfg;
    readonly ILogger<BackupService> _log;
    readonly ConcurrentDictionary<int, RunState> _runs = new();
    readonly SemaphoreSlim _gate = new(1, 1);

    sealed class RunState
    {
        public CopyProgress Progress { get; } = new();
        public CancellationTokenSource Cts { get; } = new();
        public volatile bool Queued = true;
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public DateTime? RunStartedUtc;
        public BackupResult? Result;
    }

    public BackupService(ICommandRunner run, AclService acl, IConfiguration cfg, ILogger<BackupService> log, IHostApplicationLifetime life)
    {
        (_run, _acl, _cfg, _log) = (run, acl, cfg, log);
        life.ApplicationStopping.Register(() => { foreach (var s in _runs.Values) s.Cts.Cancel(); });
    }

    IReadOnlyList<string> Roots => _cfg.GetSection("Backup:Roots").Get<string[]>() is { Length: > 0 } r ? r : ["/mnt", "/media", "/run/media"];
    bool RequireMount => _cfg["Backup:RequireMountPoint"] != "false";
    string MountInfoPath => _cfg["Backup:MountInfo"] ?? "/proc/self/mountinfo";

    IReadOnlyList<MountEntry> Mounts() =>
        File.Exists(MountInfoPath) ? MountTable.Parse(File.ReadAllText(MountInfoPath)) : [];

    // ---- Disques

    /// <summary>Normalise et vérifie un dossier cible : sous une racine autorisée et, sauf en développement, point de montage.</summary>
    public string ValidateTarget(string? dir)
    {
        var bad = new OperationException(Msg.F("“{0}” is not a mounted disk under {1}.", dir ?? "", string.Join(", ", Roots)));
        if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathRooted(dir) || dir.Any(char.IsControl)) throw bad;
        // « .. », « . » et « // » changent le chemin normalisé : refusés. (Antislash unifiés pour que les tests tournent aussi sous Windows.)
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)).Replace('\\', '/');
        if (!string.Equals(full, Path.TrimEndingDirectorySeparator(dir).Replace('\\', '/'), StringComparison.Ordinal)) throw bad;
        if (!Roots.Any(r => full.StartsWith(r.TrimEnd('/') + "/", StringComparison.Ordinal))) throw bad;
        if (RequireMount ? !Mounts().Any(m => m.MountPoint == full) : !Directory.Exists(full)) throw bad;
        return full;
    }

    /// <summary>Les disques montés sous les racines autorisées, avec le marqueur qu'ils portent éventuellement.</summary>
    public IReadOnlyList<BackupTarget> ListTargets()
    {
        var list = new List<BackupTarget>();
        foreach (var m in Mounts())
        {
            if (!Roots.Any(r => m.MountPoint.StartsWith(r.TrimEnd('/') + "/", StringComparison.Ordinal))) continue;
            long total = 0, free = 0;
            try { var d = new DriveInfo(m.MountPoint); total = d.TotalSize; free = d.AvailableFreeSpace; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            list.Add(new BackupTarget(m.MountPoint, m.Device, m.FileSystem, total, free, TargetMarker.Read(m.MountPoint)));
        }
        return list.OrderBy(t => t.Dir, StringComparer.Ordinal).ToList();
    }

    public BackupDiskStatus CheckDisk(string targetDir, string? targetId)
    {
        string dir;
        try { dir = ValidateTarget(targetDir); }
        catch (OperationException) { return new(BackupDiskState.Missing, 0, 0); }
        if (string.IsNullOrEmpty(targetId)) return new(BackupDiskState.NotInitialised, 0, 0); // « id= » vide : pas encore initialisé
        var onDisk = TargetMarker.Read(dir);
        if (onDisk is null) return new(BackupDiskState.Missing, 0, 0);
        if (onDisk != targetId) return new(BackupDiskState.OtherDisk, 0, 0);
        long free = 0, total = 0;
        try { var d = new DriveInfo(dir); free = d.AvailableFreeSpace; total = d.TotalSize; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new(BackupDiskState.Connected, free, total);
    }

    /// <summary>Écrit le marqueur du disque (ou renvoie celui qui existe déjà : initialiser deux fois n'orpheline aucun travail).</summary>
    public string InitDisk(string targetDir) => TargetMarker.Initialize(ValidateTarget(targetDir));

    // ---- Copies

    public IReadOnlyList<BackupRunStatus> Statuses() => _runs.Select(p => ToStatus(p.Key, p.Value)).OrderBy(s => s.JobId).ToList();

    /// <summary>Met une copie en file ; elle démarre dès que la précédente est terminée.</summary>
    public void Start(BackupJobDef job)
    {
        if (BackupRules.Validate(job) is { } error) throw new OperationException(error);
        var target = ValidateTarget(job.TargetDir);
        if (job.TargetId is null) throw new OperationException(Msg.F("The external disk has not been initialised for this job."));

        var state = new RunState();
        if (_runs.TryGetValue(job.Id, out var old))
        {
            if (old.Result is null) throw new OperationException(Msg.F("This job is already running."));
            _runs.TryRemove(job.Id, out _); // une copie terminée que la web n'a pas encore acquittée est remplacée
        }
        if (!_runs.TryAdd(job.Id, state)) throw new OperationException(Msg.F("This job is already running."));
        _ = Task.Run(() => RunAsync(job with { TargetDir = target }, state));
    }

    public void Cancel(int jobId) { if (_runs.TryGetValue(jobId, out var s)) s.Cts.Cancel(); }

    /// <summary>La web a enregistré la fin de la copie : l'agent peut l'oublier.</summary>
    public void Acknowledge(int jobId)
    {
        if (_runs.TryGetValue(jobId, out var s) && s.Result is not null) _runs.TryRemove(jobId, out _);
    }

    async Task RunAsync(BackupJobDef job, RunState state)
    {
        var entered = false;
        try
        {
            await _gate.WaitAsync(state.Cts.Token);
            entered = true;
            state.RunStartedUtc = DateTime.UtcNow;
            state.Queued = false;

            // Une source après l'autre ; les compteurs de la progression s'additionnent. Un partage qui échoue n'empêche pas les suivants.
            var results = new List<(BackupSource Source, CopyResult Result)>();
            foreach (var src in job.Sources)
            {
                state.Cts.Token.ThrowIfCancellationRequested();
                results.Add((src, await CopySourceAsync(job, src, state)));
            }
            state.Result = Combine(job, results);
        }
        catch (OperationCanceledException) { state.Result = new BackupResult(nameof(CopyStatus.Cancelled), "Cancelled.", [], 0); }
        catch (OperationException e) { state.Result = new BackupResult(nameof(CopyStatus.Failed), e.Message, [], 0); }
        catch (Exception e)
        {
            _log.LogError(e, "Backup job {Id} failed unexpectedly", job.Id);
            state.Result = new BackupResult(nameof(CopyStatus.Failed), e.Message, [], 0);
        }
        finally
        {
            state.Progress.Phase = "Finished";
            if (entered) _gate.Release();
            state.Result ??= new BackupResult(nameof(CopyStatus.Failed), "Interrupted.", [], 0);
        }
    }

    async Task<CopyResult> CopySourceAsync(BackupJobDef job, BackupSource src, RunState state)
    {
        string? snapshot = null;
        try
        {
            var root = await _acl.SharePathAsync(src.Share, state.Cts.Token);
            var source = root;
            if (job.Snapshot && !string.IsNullOrEmpty(src.Dataset)) // les partages hors ZFS sont copiés tels quels
            {
                snapshot = await CreateSnapshotAsync(job, src, root, state.Cts.Token);
                source = $"{root.TrimEnd('/')}/.zfs/snapshot/{snapshot[(snapshot.IndexOf('@') + 1)..]}";
                if (!Directory.Exists(source)) throw new OperationException(Msg.F("The snapshot of “{0}” cannot be read.", src.Share));
            }

            var options = new CopyOptions
            {
                SourceDir = source, TargetDir = job.TargetDir, TargetId = job.TargetId!, DestFolder = job.DestFolder,
                DestSubFolder = job.SubFolders ? src.Share : "",
                Includes = src.Includes, Excludes = job.Excludes ?? [], MirrorDeletes = job.MirrorDeletes,
                TrashRetentionDays = job.RetentionDays, Verify = job.Verify, WindowsDisk = job.WindowsDisk,
            };
            return await Task.Run(() => CopyEngine.Run(options, state.Progress, state.Cts.Token));
        }
        catch (OperationException e) { return new CopyResult(CopyStatus.Failed, e.Message, [], TimeSpan.Zero); }
        finally { if (snapshot is not null) await DestroySnapshotAsync(snapshot); }
    }

    /// <summary>Résultat unique d'un travail : le pire état des sources ; un échec partiel devient « terminé avec des erreurs ».</summary>
    static BackupResult Combine(BackupJobDef job, List<(BackupSource Source, CopyResult Result)> results)
    {
        var multi = job.Sources.Count > 1;
        string Prefix(BackupSource s) => multi ? $"{s.Share}: " : "";
        var elapsed = results.Sum(r => r.Result.Elapsed.TotalSeconds);
        var samples = results.SelectMany(r => r.Result.ErrorSamples.Select(e => multi ? $"{r.Source.Share}/{e}" : e)).Take(20).ToList();

        if (results.Any(r => r.Result.Status == CopyStatus.Cancelled))
            return new BackupResult(nameof(CopyStatus.Cancelled), "Cancelled.", samples, elapsed);
        var failed = results.Where(r => r.Result.Status == CopyStatus.Failed).ToList();
        var withErrors = results.Where(r => r.Result.Status == CopyStatus.CompletedWithErrors).ToList();
        if (failed.Count == results.Count)
            return new BackupResult(nameof(CopyStatus.Failed), string.Join(" ", failed.Select(f => Prefix(f.Source) + f.Result.Message)), samples, elapsed);
        if (failed.Count > 0 || withErrors.Count > 0)
        {
            var messages = failed.Select(f => Prefix(f.Source) + f.Result.Message).Concat(withErrors.Select(e => Prefix(e.Source) + e.Result.Message));
            return new BackupResult(nameof(CopyStatus.CompletedWithErrors), string.Join(" ", messages), samples, elapsed);
        }
        return new BackupResult(nameof(CopyStatus.Success), null, samples, elapsed);
    }

    // ---- Snapshot ZFS (cohérence : on copie une image figée, pas des fichiers qui changent pendant la copie)

    static string SnapshotName(BackupJobDef job, BackupSource src) => $"{src.Dataset}@ursacopy-{job.Id}";

    async Task<string> CreateSnapshotAsync(BackupJobDef job, BackupSource src, string shareRoot, CancellationToken ct)
    {
        if (Rules.ValidateDatasetName(src.Dataset) is { } e) throw new OperationException(e);
        // Le jeu de données doit bien être celui du partage : la web n'est pas crue sur parole.
        var mp = await _run.RunAsync("zfs", ["get", "-H", "-o", "value", "mountpoint", src.Dataset!], ct: ct);
        if (mp.ExitCode != 0 || mp.StdOut.Trim() != shareRoot)
            throw new OperationException(Msg.F("Share “{0}” is not stored on dataset “{1}”.", src.Share, src.Dataset));

        var name = SnapshotName(job, src);
        await _run.RunAsync("zfs", ["destroy", name], ct: ct); // reste d'une copie interrompue ; échoue sans gravité si absent
        var r = await _run.RunAsync("zfs", ["snapshot", name], ct: ct);
        if (r.ExitCode != 0)
            throw new OperationException(Msg.F("{0} failed ({1}): {2}", "Creating the snapshot", r.ExitCode, r.StdErr.Trim()));
        return name;
    }

    async Task DestroySnapshotAsync(string name)
    {
        try { await _run.RunAsync("zfs", ["destroy", name]); }
        catch (Exception e) { _log.LogWarning(e, "Could not remove snapshot {Name}", name); }
    }

    static BackupRunStatus ToStatus(int jobId, RunState s)
    {
        var p = s.Progress;
        return new BackupRunStatus(jobId, s.Queued, p.Phase, p.CurrentFile, Volatile.Read(ref p.CurrentFileSize), Volatile.Read(ref p.FilesSeen),
            Volatile.Read(ref p.BytesSeen), Volatile.Read(ref p.Copied), Volatile.Read(ref p.BytesCopied), Volatile.Read(ref p.Unchanged),
            Volatile.Read(ref p.Deleted), Volatile.Read(ref p.Skipped), Volatile.Read(ref p.Errors), s.StartedUtc, s.RunStartedUtc, s.Result);
    }
}
