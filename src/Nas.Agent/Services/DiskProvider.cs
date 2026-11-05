using System.Text.Json;
using Nas.Contracts;

namespace Nas.Agent.Services;

public interface IDiskProvider
{
    Task<IReadOnlyList<DiskInfo>> ListAsync(CancellationToken ct = default);
}

public static class LsblkParser
{
    public static readonly string[] Args =
        ["--json", "--bytes", "--output", "NAME,PATH,SIZE,TYPE,MODEL,SERIAL,ROTA,MOUNTPOINTS"];

    /// <summary>Extrait les disques (type "disk") de la sortie JSON de lsblk, y compris les points de montage des partitions.</summary>
    public static IReadOnlyList<DiskInfo> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<DiskInfo>();
        if (!doc.RootElement.TryGetProperty("blockdevices", out var devices)) return result;

        foreach (var d in devices.EnumerateArray())
        {
            if (Str(d, "type") != "disk") continue;
            var mounts = new List<string>();
            CollectMounts(d, mounts);
            result.Add(new DiskInfo(
                Name: Str(d, "name") ?? "",
                Path: Str(d, "path") ?? "",
                SizeBytes: Long(d, "size"),
                Model: Str(d, "model")?.Trim(),
                Serial: Str(d, "serial")?.Trim(),
                Rotational: Bool(d, "rota"),
                MountPoints: mounts));
        }
        return result;
    }

    private static void CollectMounts(JsonElement dev, List<string> into)
    {
        if (dev.TryGetProperty("mountpoints", out var mp) && mp.ValueKind == JsonValueKind.Array)
            foreach (var m in mp.EnumerateArray())
                if (m.ValueKind == JsonValueKind.String && m.GetString() is { Length: > 0 } s) into.Add(s);
        if (dev.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
            foreach (var c in ch.EnumerateArray()) CollectMounts(c, into);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // lsblk renvoie selon la version un nombre, une chaîne ou un booléen.
    private static long Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetInt64(),
            JsonValueKind.String when long.TryParse(v.GetString(), out var n) => n,
            _ => 0,
        };
    }

    private static bool? Bool(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.GetInt32() != 0,
            JsonValueKind.String => v.GetString() is "1" or "true",
            _ => null,
        };
    }
}

public sealed class LsblkDiskProvider(ICommandRunner runner) : IDiskProvider
{
    public async Task<IReadOnlyList<DiskInfo>> ListAsync(CancellationToken ct = default)
    {
        var r = await runner.RunAsync("lsblk", LsblkParser.Args, ct: ct);
        if (r.ExitCode != 0) throw new InvalidOperationException($"lsblk a échoué ({r.ExitCode}) : {r.StdErr}");
        return LsblkParser.Parse(r.StdOut);
    }
}

/// <summary>Données fictives pour développer sans disque réel (URSANAS_FAKE=1).</summary>
public sealed class FakeDiskProvider : IDiskProvider
{
    public Task<IReadOnlyList<DiskInfo>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DiskInfo>>(
        [
            new("sda", "/dev/sda", 20L << 30, "Virtual Disk", "SYS001", false, ["/", "/boot/efi"]),
            new("sdb", "/dev/sdb", 10L << 30, "Virtual Disk", "DAT001", false, []),
            new("sdc", "/dev/sdc", 10L << 30, "Virtual Disk", "DAT002", false, []),
        ]);
}
