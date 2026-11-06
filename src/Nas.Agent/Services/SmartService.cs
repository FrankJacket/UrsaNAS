using System.ComponentModel;
using System.Text.Json;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>Lecture de la sortie JSON de <c>smartctl -j -a</c> (disques ATA et NVMe).</summary>
public static class SmartParser
{
    public static SmartInfo Parse(string disk, string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return new SmartInfo(disk, "unsupported", null, null, null, null, "Réponse de smartctl illisible."); }

        using (doc)
        {
            var root = doc.RootElement;
            int? temp = Int(root, "temperature", "current");
            long? hours = Long(root, "power_on_time", "hours");
            long? reallocated = null, pending = null;
            var notes = new List<string>();

            if (root.TryGetProperty("ata_smart_attributes", out var ata) && ata.TryGetProperty("table", out var table))
            {
                foreach (var a in table.EnumerateArray())
                {
                    var id = a.TryGetProperty("id", out var i) && i.TryGetInt32(out var iv) ? iv : -1;
                    var raw = Long(a, "raw", "value");
                    if (id == 5) reallocated = raw;
                    if (id is 197 or 198) pending = (pending ?? 0) + (raw ?? 0);
                }
            }

            bool? passed = null;
            if (Dig(root, ["smart_status", "passed"]) is { } p && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False))
                passed = p.GetBoolean();

            var nvmeFailing = false;
            if (root.TryGetProperty("nvme_smart_health_information_log", out var nvme))
            {
                if ((Long(nvme, "critical_warning") ?? 0) != 0) { nvmeFailing = true; notes.Add("avertissement critique NVMe"); }
                if ((Long(nvme, "media_errors") ?? 0) > 0) notes.Add($"{Long(nvme, "media_errors")} erreurs de média");
                temp ??= Int(nvme, "temperature");
            }

            if (passed is null && !nvmeFailing)
                return new SmartInfo(disk, "unsupported", temp, hours, reallocated, pending, FirstMessage(root) ?? "SMART indisponible sur ce disque.");

            if (reallocated > 0) notes.Add($"{reallocated} secteurs réalloués");
            if (pending > 0) notes.Add($"{pending} secteurs en attente");

            var status = passed == false || nvmeFailing ? "failing" : notes.Count > 0 ? "warning" : "ok";
            if (passed == false) notes.Insert(0, "le disque annonce sa défaillance");
            return new SmartInfo(disk, status, temp, hours, reallocated, pending, notes.Count > 0 ? string.Join(", ", notes) : null);
        }
    }

    static string? FirstMessage(JsonElement root) =>
        root.TryGetProperty("smartctl", out var s) && s.TryGetProperty("messages", out var m) && m.ValueKind == JsonValueKind.Array
            && m.GetArrayLength() > 0 && m[0].TryGetProperty("string", out var t) ? t.GetString() : null;

    static JsonElement? Dig(JsonElement e, string[] path)
    {
        foreach (var name in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out e)) return null;
        }
        return e;
    }

    static long? Long(JsonElement e, params string[] path) =>
        Dig(e, path) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var v) ? v : null;

    static int? Int(JsonElement e, params string[] path) => Long(e, path) is { } v ? (int)v : null;
}

public sealed class SmartService(ICommandRunner run, IDiskProvider disks)
{
    public async Task<IReadOnlyList<SmartInfo>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<SmartInfo>();
        foreach (var d in await disks.ListAsync(ct))
        {
            CommandResult r;
            try { r = await run.RunAsync("smartctl", ["-j", "-a", d.Path], ct: ct); }
            catch (Win32Exception) { throw new OperationException("smartmontools n'est pas installé sur le NAS (apt install smartmontools)."); }

            // smartctl renvoie un code de sortie par bits, même quand le JSON est complet : seul le JSON compte.
            result.Add(string.IsNullOrWhiteSpace(r.StdOut)
                ? new SmartInfo(d.Path, "unsupported", null, null, null, null, r.StdErr.Trim())
                : SmartParser.Parse(d.Path, r.StdOut));
        }
        return result;
    }
}
