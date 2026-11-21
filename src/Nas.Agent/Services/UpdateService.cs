using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>
/// Recherche de mises à jour : la version d'UrsaNAS (releases GitHub du dépôt public) et les paquets Linux (apt).
/// Cette étape ne fait que lire : rien n'est installé.
/// </summary>
public sealed partial class UpdateService(ICommandRunner runner, IConfiguration cfg, ILogger<UpdateService> log, HttpMessageHandler? handler = null)
{
    readonly HttpClient _http = new(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(30) };
    readonly SemaphoreSlim _apt = new(1, 1);
    readonly object _lock = new();
    AppUpdateInfo? _app;
    PackageUpdates? _pkgs;
    string? _fakeChannel;
    AppInstallStatus? _fakeInstall;
    bool _installing;
    PackageInstallProgress _progress = new(false, [], [], null);

    bool Fake => cfg["URSANAS_FAKE"] == "1";
    string SettingsFile => cfg["Update:SettingsFile"] ?? "/var/lib/ursanas/update-settings.json";
    string VersionFile => cfg["Update:VersionFile"] ?? "/opt/ursanas/VERSION";
    string RebootFile => cfg["Update:RebootRequiredFile"] ?? "/var/run/reboot-required";
    string Repo => cfg["Update:Repository"] ?? "FrankJacket/UrsaNAS";
    string ApiBase => (cfg["Update:ApiUrl"] ?? "https://api.github.com").TrimEnd('/');
    string WorkDir => cfg["Update:WorkDir"] ?? "/var/lib/ursanas/update";
    string StatusFile => Path.Combine(WorkDir, "status.json");
    string ApplyScript => cfg["Update:ApplyScript"] ?? "/opt/ursanas/update-apply.sh";

    static readonly JsonSerializerOptions StatusJsonOptions = new() { PropertyNameCaseInsensitive = true };

    sealed record Stored(string Channel);

    public UpdateStatus Status()
    {
        lock (_lock) return new UpdateStatus(GetSettings(), _app, _pkgs);
    }

    public UpdateSettings GetSettings()
    {
        if (Fake) return new UpdateSettings(_fakeChannel ?? "stable");
        var channel = "stable";
        try
        {
            if (File.Exists(SettingsFile) && JsonSerializer.Deserialize<Stored>(File.ReadAllText(SettingsFile)) is { } s && Rules.UpdateChannels.Contains(s.Channel)) channel = s.Channel;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { log.LogWarning(e, "Update settings unreadable"); }
        return new UpdateSettings(channel);
    }

    public void SetSettings(SetUpdateSettingsRequest r)
    {
        if (!Rules.UpdateChannels.Contains(r.Channel)) throw new OperationException(Msg.F("Unknown update channel “{0}”.", r.Channel));
        if (Fake) { _fakeChannel = r.Channel; return; }
        var dir = Path.GetDirectoryName(SettingsFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new Stored(r.Channel)));
    }

    /// <summary>Version installée : le fichier VERSION posé par install.sh, sinon celle du programme.</summary>
    public string CurrentVersion()
    {
        try { if (File.Exists(VersionFile) && File.ReadAllText(VersionFile).Trim() is { Length: > 0 } v) return v; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }

    // ---- UrsaNAS

    public async Task<AppUpdateInfo> CheckAppAsync(CancellationToken ct)
    {
        var current = CurrentVersion();
        var result = Fake ? FakeApp(current) : await QueryReleasesAsync(current, ct);
        lock (_lock) _app = result;
        return result;
    }

    AppUpdateInfo FakeApp(string current)
    {
        var pre = GetSettings().Channel == "prerelease";
        var latest = pre ? "9.9.0-rc1" : "9.9.0";
        return new AppUpdateInfo(current, latest, true, "Version simulée.", "https://github.com/FrankJacket/UrsaNAS/releases", pre,
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow, null, "v" + latest);
    }

    async Task<AppUpdateInfo> QueryReleasesAsync(string current, CancellationToken ct)
    {
        var settings = GetSettings();
        AppUpdateInfo Fail(string msg) => new(current, null, false, null, null, false, null, DateTimeOffset.UtcNow, msg);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/repos/{Repo}/releases?per_page=30");
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("UrsaNAS", "1"));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var r = await _http.SendAsync(req, ct);
            if (!r.IsSuccessStatusCode) return Fail(Msg.F("GitHub answered {0}.", (int)r.StatusCode));
            return Pick(current, settings.Channel == "prerelease", await r.Content.ReadAsStringAsync(ct));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            log.LogWarning(e, "Release check failed");
            return Fail(Msg.F("Could not reach GitHub: check the Internet connection of this machine."));
        }
    }

    /// <summary>Choisit la plus récente des releases publiées (hors brouillons) du canal, et la compare à la version installée.</summary>
    public static AppUpdateInfo Pick(string current, bool prereleases, string json)
    {
        using var doc = JsonDocument.Parse(json);
        string? bestTag = null; Version? best = null; JsonElement bestEl = default;
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            var pre = rel.TryGetProperty("prerelease", out var p) && p.GetBoolean();
            if (pre && !prereleases) continue;
            var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (!TryParse(tag, out var v)) continue;
            if (best is null || v > best) { best = v; bestTag = tag; bestEl = rel.Clone(); }
        }
        var now = DateTimeOffset.UtcNow;
        if (best is null) return new AppUpdateInfo(current, null, false, null, null, false, null, now, null);
        string? Str(string n) => bestEl.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var notes = Str("body");
        if (notes?.Length > 4000) notes = notes[..4000] + "…";
        DateTimeOffset? published = DateTimeOffset.TryParse(Str("published_at"), out var pa) ? pa : null;
        var available = TryParse(current, out var cur) ? best > cur : bestTag!.TrimStart('v') != current;
        return new AppUpdateInfo(current, bestTag!.TrimStart('v'), available, notes, Str("html_url"),
            bestEl.TryGetProperty("prerelease", out var pp) && pp.GetBoolean(), published, now, null, bestTag);
    }

    /// <summary>« v1.2.3 », « 1.2.3 » ou « 1.2.3-rc1 ». Une préversion vaut un peu moins que la version finale du même numéro.</summary>
    static bool TryParse(string? s, out Version version)
    {
        version = new Version(0, 0);
        var m = s is null ? null : VersionRegex().Match(s.Trim());
        if (m is null || !m.Success) return false;
        version = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), m.Groups[4].Success ? 0 : 1);
        return true;
    }

    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.\-]+)?$")]
    private static partial Regex VersionRegex();

    // ---- Installation d'UrsaNAS

    /// <summary>
    /// Télécharge la release déjà repérée par <see cref="CheckAppAsync"/>, vérifie sa somme de contrôle, puis lance son installation
    /// détachée (<c>update-apply.sh</c> via systemd-run) : elle survit au redémarrage de l'agent que <c>install.sh</c> déclenche.
    /// </summary>
    public async Task InstallAppAsync(CancellationToken ct)
    {
        if (Fake)
        {
            var v = _app?.Latest ?? "9.9.0";
            _fakeInstall = new AppInstallStatus("done", v, Msg.F("Update installed"), DateTimeOffset.UtcNow);
            return;
        }
        if (_installing) throw new OperationException(Msg.F("An update is already being installed."));
        var app = _app;
        if (app is not { Available: true, Tag: not null, Latest: not null }) throw new OperationException(Msg.F("Check for updates first."));
        _installing = true;
        try
        {
            if (!File.Exists(ApplyScript))
                throw new OperationException(Msg.F("The update helper script is missing ({0}): reinstall UrsaNAS once by hand to add it.", ApplyScript));
            WriteInstallStatus("running", app.Latest, Msg.F("Downloading {0}", app.Latest));
            var workDir = await DownloadAndExtractAsync(app.Tag, app.Latest, ct);
            WriteInstallStatus("running", app.Latest, Msg.F("Applying the update"));
            var unit = $"ursanas-update-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            var r = await runner.RunAsync("systemd-run", ["--unit=" + unit, "--collect", "--", "/bin/bash", ApplyScript, workDir, StatusFile], ct: ct);
            if (r.ExitCode != 0) throw new OperationException(Msg.F("Could not start the update: {0}", FirstLine(r.StdErr)));
        }
        catch (OperationException ex)
        {
            WriteInstallStatus("failed", app.Latest, ex.Message);
            throw;
        }
        finally { _installing = false; }
    }

    public AppInstallStatus GetInstallStatus()
    {
        if (Fake) return _fakeInstall ?? new AppInstallStatus("idle", null, null, DateTimeOffset.UtcNow);
        try
        {
            if (File.Exists(StatusFile) && JsonSerializer.Deserialize<AppInstallStatus>(File.ReadAllText(StatusFile), StatusJsonOptions) is { } s) return s;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { log.LogWarning(e, "Install status unreadable"); }
        return new AppInstallStatus("idle", null, null, DateTimeOffset.UtcNow);
    }

    void WriteInstallStatus(string stage, string? version, string? message)
    {
        Directory.CreateDirectory(WorkDir);
        File.WriteAllText(StatusFile, JsonSerializer.Serialize(new AppInstallStatus(stage, version, message, DateTimeOffset.UtcNow)));
    }

    async Task<JsonElement> FetchReleaseAsync(string tag, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/repos/{Repo}/releases/tags/{Uri.EscapeDataString(tag)}");
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("UrsaNAS", "1"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var r = await _http.SendAsync(req, ct);
        if (!r.IsSuccessStatusCode) throw new OperationException(Msg.F("GitHub answered {0} for release {1}.", (int)r.StatusCode, tag));
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone();
    }

    async Task<byte[]> DownloadAssetAsync(JsonElement release, string assetName, CancellationToken ct)
    {
        if (release.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
                if (a.TryGetProperty("name", out var n) && n.GetString() == assetName && a.TryGetProperty("url", out var u) && u.GetString() is { } url)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                    req.Headers.UserAgent.Add(new ProductInfoHeaderValue("UrsaNAS", "1"));
                    using var r = await _http.SendAsync(req, ct);
                    if (!r.IsSuccessStatusCode) throw new OperationException(Msg.F("GitHub answered {0} for {1}.", (int)r.StatusCode, assetName));
                    return await r.Content.ReadAsByteArrayAsync(ct);
                }
        throw new OperationException(Msg.F("Release asset “{0}” was not found.", assetName));
    }

    async Task<string> DownloadAndExtractAsync(string tag, string version, CancellationToken ct)
    {
        JsonElement release;
        try { release = await FetchReleaseAsync(tag, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new OperationException(Msg.F("Could not reach GitHub: check the Internet connection of this machine."));
        }
        var baseName = $"ursanas-{version}-linux-x64";
        byte[] tar, sha;
        try
        {
            tar = await DownloadAssetAsync(release, $"{baseName}.tar.gz", ct);
            sha = await DownloadAssetAsync(release, $"{baseName}.tar.gz.sha256", ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new OperationException(Msg.F("Could not reach GitHub: check the Internet connection of this machine."));
        }
        var expected = System.Text.Encoding.UTF8.GetString(sha).AsSpan().TrimStart();
        var sep = expected.IndexOfAny(' ', '\t');
        if (sep > 0) expected = expected[..sep];
        var actual = Convert.ToHexStringLower(SHA256.HashData(tar));
        if (!expected.Equals(actual, StringComparison.OrdinalIgnoreCase)) throw new OperationException(Msg.F("The downloaded file does not match its checksum."));

        Directory.CreateDirectory(WorkDir);
        var extractDir = Path.Combine(WorkDir, baseName);
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        Directory.CreateDirectory(extractDir);
        try
        {
            await using var ms = new MemoryStream(tar);
            await using var gz = new GZipStream(ms, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gz, extractDir, overwriteFiles: true, ct);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            throw new OperationException(Msg.F("The downloaded archive is not valid: {0}", e.Message));
        }
        var inner = Path.Combine(extractDir, baseName); // l'archive contient un dossier « ursanas-X.Y.Z-linux-x64 » : on redescend dedans s'il existe
        return Directory.Exists(inner) ? inner : extractDir;
    }

    // ---- Paquets Linux

    static readonly string[] ManualPrefixes =
    [
        "linux-image", "linux-headers", "linux-modules", "linux-base", "zfs", "libzfs", "libnvpair", "libuutil", "libzpool", "spl",
        "docker", "containerd", "runc", "libvirt", "qemu", "ovmf", "swtpm", "openvswitch",
    ];

    static readonly string[] TrackedPrefixes =
    [
        "samba", "smbclient", "libsmbclient", "winbind", "nfs-", "vsftpd", "smartmontools", "rclone", "netplan", "ifupdown", "openssh", "ca-certificates",
        "openssl", "curl", "xfsprogs", "btrfs-progs", "exfatprogs", "acl", "cifs-utils",
    ];

    static bool Starts(string name, string[] prefixes) => prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    public async Task<PackageUpdates> CheckPackagesAsync(CancellationToken ct)
    {
        await _apt.WaitAsync(ct);
        try
        {
            var result = Fake ? FakePackages() : await QueryPackagesAsync(ct);
            lock (_lock) _pkgs = result;
            return result;
        }
        finally { _apt.Release(); }
    }

    static PackageUpdates FakePackages() => new(
    [
        new("samba", "2:4.22.3+dfsg-1", "2:4.22.4+dfsg-1", true, false),
        new("smartmontools", "7.4-3", "7.5-1", true, false),
        new("docker.io", "26.1.5-1", "26.1.6-1", true, true),
        new("linux-image-amd64", "6.12.43-1", "6.12.48-1", true, true),
        new("tzdata", "2025b-5", "2025c-1", false, false),
    ], true, DateTimeOffset.UtcNow, null);

    async Task<PackageUpdates> QueryPackagesAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var up = await runner.RunAsync("apt-get", ["update", "-qq"], ct: ct);
        var warn = up.ExitCode != 0 ? Msg.F("The package lists could not be refreshed: {0}", FirstLine(up.StdErr)) : null;
        var list = await runner.RunAsync("apt", ["list", "--upgradable"], ct: ct);
        if (list.ExitCode != 0) return new PackageUpdates([], false, now, Msg.F("Could not list the available package updates: {0}", FirstLine(list.StdErr)));
        return new PackageUpdates(ParseUpgradable(list.StdOut), File.Exists(RebootFile), now, warn);
    }

    static string FirstLine(string s)
    {
        var l = s.Trim().Split('\n')[0].Trim();
        return l.Length > 200 ? l[..200] : l;
    }

    /// <summary>Lit « nom/suite version arch [upgradable from: ancienne] » (le libellé entre crochets dépend de la langue : seule la dernière valeur compte).</summary>
    public static IReadOnlyList<PackageUpdate> ParseUpgradable(string output)
    {
        var items = new List<PackageUpdate>();
        foreach (var line in output.Split('\n'))
        {
            var m = UpgradableRegex().Match(line.Trim());
            if (!m.Success) continue;
            var name = m.Groups[1].Value;
            var manual = Starts(name, ManualPrefixes);
            items.Add(new PackageUpdate(name, m.Groups[3].Value, m.Groups[2].Value, manual || Starts(name, TrackedPrefixes), manual));
        }
        return items.OrderByDescending(i => i.Tracked).ThenBy(i => i.Name, StringComparer.Ordinal).ToList();
    }

    [GeneratedRegex(@"^([a-z0-9][a-z0-9+.\-]*)/\S+\s+(\S+)\s+\S+\s+\[[^\]]*?(\S+)\]$")]
    private static partial Regex UpgradableRegex();

    /// <summary>
    /// Installe les paquets demandés (<c>apt-get install --only-upgrade</c>) : seuls ceux de la dernière vérification sont acceptés,
    /// qu'ils soient « Manuel seulement » ou non (ce choix n'est appliqué qu'à la mise à jour planifiée, pas ici).
    /// </summary>
    public async Task InstallPackagesAsync(InstallPackagesRequest r, CancellationToken ct)
    {
        var names = r.Names.Distinct().ToList();
        if (names.Count == 0) throw new OperationException(Msg.F("No package was selected."));
        foreach (var n in names) if (!Rules.IsPackageName(n)) throw new OperationException(Msg.F("“{0}” is not a valid package name.", n));
        if (Fake) { await FakeInstallAsync(names, ct); return; }
        await _apt.WaitAsync(ct);
        try
        {
            var known = _pkgs?.Items.Select(i => i.Name).ToHashSet(StringComparer.Ordinal) ?? [];
            var unknown = names.FirstOrDefault(n => !known.Contains(n));
            if (unknown is not null) throw new OperationException(Msg.F("“{0}” was not in the last check: check for updates again.", unknown));
            SetProgress(names);
            string[] args = ["install", "-y", "--only-upgrade", .. names];
            var r2 = runner is IStreamingCommandRunner streaming
                ? await streaming.RunAsync("apt-get", args, AddProgressLine, ct)
                : await runner.RunAsync("apt-get", args, ct: ct);
            if (r2.ExitCode != 0) throw new OperationException(Msg.F("The package installation failed: {0}", FirstLine(r2.StdErr)));
            _pkgs = await QueryPackagesAsync(ct);
        }
        finally
        {
            lock (_lock) _progress = _progress with { Running = false };
            _apt.Release();
        }
    }

    /// <summary>Ce que fait l'installation de paquets en ce moment (pour l'afficher pendant que la demande est en cours).</summary>
    public PackageInstallProgress GetPackageProgress() { lock (_lock) return _progress; }

    void SetProgress(IReadOnlyList<string> names)
    {
        lock (_lock) _progress = new PackageInstallProgress(true, names, [], DateTimeOffset.UtcNow);
    }

    void AddProgressLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return;
        if (line.Length > 200) line = line[..200];
        lock (_lock) _progress = _progress with { Lines = [.. _progress.Lines.TakeLast(11), line] };
    }

    /// <summary>Mode simulé : quelques lignes espacées dans le temps, pour voir l'affichage sans toucher au système.</summary>
    async Task FakeInstallAsync(List<string> names, CancellationToken ct)
    {
        SetProgress(names);
        try
        {
            AddProgressLine("Reading package lists...");
            foreach (var n in names)
            {
                await Task.Delay(1200, ct); AddProgressLine($"Get:1 http://deb.debian.org/debian stable/main amd64 {n} amd64 [1 234 kB]");
                await Task.Delay(1200, ct); AddProgressLine($"Unpacking {n} ...");
                await Task.Delay(1200, ct); AddProgressLine($"Setting up {n} ...");
            }
        }
        finally { lock (_lock) _progress = _progress with { Running = false }; }
    }
}
