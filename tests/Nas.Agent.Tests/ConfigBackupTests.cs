using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Extensions.Configuration;
using Nas.Contracts;
using Nas.Web;
using Nas.Web.Alerting;
using Nas.Web.Data;
using Xunit;

namespace Nas.Agent.Tests;

sealed class FakeConfigAgentHandler : HttpMessageHandler
{
    public bool Unreachable;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Unreachable) throw new HttpRequestException("unreachable");
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = JsonContent.Create(o) };
        var response = (request.Method.Method, path) switch
        {
            ("GET", "users") => Json(new[] { new NasUser("alice") }),
            ("GET", "groups") => Json(new[] { new NasGroup("staff", ["alice"]) }),
            ("GET", "network") => Json(new NetSnapshot(0, [], "netplan")),
            ("GET", "network/ovs") => Json(new OvsStatus(false, false, true, null, [])),
            ("GET", "system/update") => Json(new UpdateStatus(new UpdateSettings("stable", false), null, null)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        return Task.FromResult(response);
    }
}

public class ConfigBackupTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-cfgbackup-" + Guid.NewGuid().ToString("N"));
    readonly Repo _repo;
    readonly BackupRepo _backups;
    readonly FakeConfigAgentHandler _handler = new();
    readonly ConfigBackupOperations _ops;

    public ConfigBackupTests()
    {
        var db = new Db(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = _dir }).Build());
        db.Init();
        _repo = new Repo(db);
        _backups = new BackupRepo(db);
        var agent = new AgentClient(new HttpClient(_handler) { BaseAddress = new Uri("http://agent/") });
        _ops = new ConfigBackupOperations(_repo, _backups, agent);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    async Task SeedAsync()
    {
        await _repo.UpsertShareAsync(new ShareDef("docs", "Documents", false, ["alice"]));
        await _repo.SetFolderAclAsync("docs", "/", [new AclRule(AclSubjectKind.User, "alice", true, AclRights.ListData, AclApply.Everything)]);
        await _backups.SaveJobAsync(new BackupJobRow { Name = "nightly", TargetDir = "/mnt/backup", DestFolder = "nas" });
        await _repo.SaveMailSettingsAsync(new MailSettings(true, "smtp.example.com", 587, "starttls", "alerts", "super-secret", "nas@example.com", "admin@example.com"));
    }

    static Dictionary<string, byte[]> ReadEntries(byte[] zip, string? password)
    {
        using var mem = new MemoryStream(zip);
        using var zf = new ZipFile(mem) { Password = password };
        var result = new Dictionary<string, byte[]>();
        foreach (ZipEntry entry in zf)
        {
            using var s = zf.GetInputStream(entry);
            using var outMem = new MemoryStream();
            s.CopyTo(outMem);
            result[entry.Name] = outMem.ToArray();
        }
        return result;
    }

    [Fact]
    public async Task Le_zip_sans_mot_de_passe_contient_les_elements_attendus()
    {
        await SeedAsync();
        var zip = await _ops.ExportAsync(null, "tester", default);
        var entries = ReadEntries(zip, null);

        Assert.Contains("manifest.json", entries.Keys);
        Assert.Contains("shares.json", entries.Keys);
        Assert.Contains("folder-acls.json", entries.Keys);
        Assert.Contains("backup-jobs.json", entries.Keys);
        Assert.Contains("ftp-settings.json", entries.Keys);
        Assert.Contains("update-schedule.json", entries.Keys);
        Assert.Contains("mail-settings.json", entries.Keys);
        Assert.Contains("users.json", entries.Keys);
        Assert.Contains("groups.json", entries.Keys);
        Assert.Contains("network.json", entries.Keys);

        var shares = JsonSerializer.Deserialize<List<ShareDef>>(entries["shares.json"])!;
        Assert.Equal("docs", Assert.Single(shares).Name);

        var mail = JsonSerializer.Deserialize<MailSettings>(entries["mail-settings.json"])!;
        Assert.Equal("", mail.Password); // jamais le mot de passe SMTP
        Assert.Equal("alerts", mail.User);
    }

    [Fact]
    public async Task Le_journal_garde_une_trace_du_telechargement()
    {
        await _ops.ExportAsync(null, "tester", default);
        var entry = Assert.Single(await _repo.GetAuditAsync());
        Assert.Equal(("tester", "Configuration backup downloaded"), (entry.Actor, entry.Action));
    }

    [Fact]
    public async Task Un_mot_de_passe_protege_vraiment_le_zip()
    {
        var zip = await _ops.ExportAsync("correct-horse", "tester", default);
        using var mem = new MemoryStream(zip);
        Assert.Throws<ZipException>(() =>
        {
            using var zf = new ZipFile(mem);
            foreach (ZipEntry entry in zf) { using var s = zf.GetInputStream(entry); } // sans mot de passe : échec à la lecture
        });

        var entries = ReadEntries(zip, "correct-horse");
        Assert.Contains("manifest.json", entries.Keys);
    }

    [Fact]
    public async Task Sans_agent_joignable_le_reste_est_quand_meme_exporte()
    {
        _handler.Unreachable = true;
        var zip = await _ops.ExportAsync(null, "tester", default);
        var entries = ReadEntries(zip, null);
        Assert.Contains("manifest.json", entries.Keys);
        Assert.DoesNotContain("users.json", entries.Keys);
    }

    [Fact]
    public async Task Un_mot_de_passe_trop_long_est_rejete() =>
        await Assert.ThrowsAsync<AgentException>(() => _ops.ExportAsync(new string('x', 200), "tester", default));
}
