using System.Text;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;
using Nas.Contracts;
using Nas.Web.Data;

namespace Nas.Web;

/// <summary>
/// Sauvegarde des réglages du NAS en ZIP (JSON par élément), pour pouvoir le reconfigurer après une réinitialisation complète.
/// Jamais de mot de passe dedans (comptes NAS, SMTP) : seuls les noms, groupes et réglages sont repris.
/// Appelée depuis une route HTTP ordinaire (pas un composant Blazor) : l'acteur vient du <see cref="HttpContext"/>, jamais d'<c>AuthenticationStateProvider</c>.
/// </summary>
public sealed class ConfigBackupOperations(Repo repo, BackupRepo backups, AgentClient agent)
{
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public async Task<byte[]> ExportAsync(string? password, string actor, CancellationToken ct)
    {
        if (!Rules.IsZipPassword(password)) throw new AgentException(Msg.F("This password is too long."));

        var files = new Dictionary<string, string> { ["manifest.json"] = Serialize(new { Product = "UrsaNAS", ExportedAtUtc = DateTimeOffset.UtcNow }) };

        var shares = await repo.GetSharesAsync();
        files["shares.json"] = Serialize(shares);
        var folderAcls = new Dictionary<string, Dictionary<string, IReadOnlyList<AclRule>>>();
        foreach (var s in shares) folderAcls[s.Name] = await repo.GetFolderAclsAsync(s.Name);
        files["folder-acls.json"] = Serialize(folderAcls);
        files["backup-jobs.json"] = Serialize(await backups.GetJobsAsync());
        files["ftp-settings.json"] = Serialize(await repo.GetFtpSettingsAsync());
        files["update-schedule.json"] = Serialize(await repo.GetUpdateScheduleAsync());
        files["mail-settings.json"] = Serialize((await repo.GetMailSettingsAsync()) with { Password = "" }); // le mot de passe SMTP doit être ressaisi après restauration

        try
        {
            files["users.json"] = Serialize(await agent.GetUsersAsync(ct));
            files["user-profiles.json"] = Serialize((await repo.GetUserProfilesAsync()).Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase));
            files["groups.json"] = Serialize(await agent.GetGroupsAsync(ct));
            files["network.json"] = Serialize(await agent.GetNetworkAsync(ct));
            files["open-vswitch.json"] = Serialize(await agent.GetOvsAsync(ct));
            files["update-source.json"] = Serialize(new { Channel = (await agent.GetUpdateStatusAsync(ct)).Settings.Channel });
        }
        catch (HttpRequestException) { /* agent injoignable : on conserve quand même ce qui vient de la base */ }

        var zip = BuildZip(files, password);
        await repo.AuditAsync(actor, "Configuration backup downloaded", password is { Length: > 0 } ? "password-protected" : "no password");
        return zip;
    }

    static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOpts);

    static byte[] BuildZip(Dictionary<string, string> files, string? password)
    {
        using var mem = new MemoryStream();
        using (var zip = new ZipOutputStream(mem) { IsStreamOwner = false })
        {
            if (!string.IsNullOrEmpty(password)) zip.Password = password;
            zip.SetLevel(6);
            foreach (var (name, content) in files)
            {
                var entry = new ZipEntry(name) { DateTime = DateTime.UtcNow };
                if (!string.IsNullOrEmpty(password)) entry.AESKeySize = 256;
                zip.PutNextEntry(entry);
                var bytes = Encoding.UTF8.GetBytes(content);
                zip.Write(bytes, 0, bytes.Length);
                zip.CloseEntry();
            }
            zip.Finish();
        }
        return mem.ToArray();
    }
}
