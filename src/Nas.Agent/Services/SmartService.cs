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
        catch (JsonException) { return new SmartInfo(disk, "unsupported", null, null, null, null, "Unreadable smartctl response."); }

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
                if ((Long(nvme, "critical_warning") ?? 0) != 0) { nvmeFailing = true; notes.Add("NVMe critical warning"); }
                if ((Long(nvme, "media_errors") ?? 0) > 0) notes.Add(Msg.F("{0} media errors", Long(nvme, "media_errors")));
                temp ??= Int(nvme, "temperature");
            }

            if (passed is null && !nvmeFailing)
                return new SmartInfo(disk, "unsupported", temp, hours, reallocated, pending, FirstMessage(root) ?? "SMART unavailable on this disk.");

            if (reallocated > 0) notes.Add(Msg.F("{0} reallocated sectors", reallocated));
            if (pending > 0) notes.Add(Msg.F("{0} pending sectors", pending));

            var status = passed == false || nvmeFailing ? "failing" : notes.Count > 0 ? "warning" : "ok";
            if (passed == false) notes.Insert(0, "the disk reports its own failure");
            return new SmartInfo(disk, status, temp, hours, reallocated, pending, notes.Count > 0 ? string.Join(", ", notes) : null);
        }
    }

    /// <summary>
    /// The disk's self-test state from <c>smartctl -j -a</c>: the test in progress, or else the result of the latest one.
    /// ATA, NVMe and SCSI each use their own keys.
    /// </summary>
    public static (SelfTestState State, int? RemainingPercent, string? Detail) ParseSelfTest(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return (SelfTestState.Unsupported, null, "Unreadable smartctl response."); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (SelfTestState.Unsupported, null, "Unreadable smartctl response.");
            if (root.TryGetProperty("ata_smart_data", out var ata))
            {
                var read = ParseAtaSelfTest(ata);
                // An ATA disk never tested reports the same status as a successful test: only its empty log tells them apart.
                var entries = Long(root, "ata_smart_self_test_log", "standard", "count") ?? Long(root, "ata_smart_self_test_log", "extended", "count");
                return read.Item1 == SelfTestState.Passed && entries == 0 ? (SelfTestState.None, null, null) : read;
            }
            if (root.TryGetProperty("nvme_self_test_log", out var nvme)) return ParseNvmeSelfTest(nvme);
            if (root.TryGetProperty("scsi_self_test_0", out var scsi)) return ParseScsiSelfTest(scsi);
            // A SCSI disk never tested has no log entry: it can only be recognized by its working SMART.
            if (Dig(root, ["device", "protocol"]) is { ValueKind: JsonValueKind.String } protocol && protocol.GetString() == "SCSI"
                && Dig(root, ["smart_status", "passed"]) is not null)
                return (SelfTestState.None, null, null);
            return (SelfTestState.Unsupported, null, FirstMessage(root));
        }
    }

    static (SelfTestState, int?, string?) ParseAtaSelfTest(JsonElement ata)
    {
        if (Dig(ata, ["capabilities", "self_tests_supported"]) is not { ValueKind: JsonValueKind.True }) return (SelfTestState.Unsupported, null, null);
        if (Long(ata, "self_test", "status", "value") is not { } value) return (SelfTestState.None, null, null);
        var detail = Text(ata, "self_test", "status", "string");
        return (value >> 4) switch // the high half of the byte is the result, the low half the tenths left to do
        {
            0xF => (SelfTestState.Running, (int)(value & 0xF) * 10, detail),
            0 => (SelfTestState.Passed, null, detail),
            1 or 2 => (SelfTestState.Aborted, null, detail), // aborted by the host, or by a restart
            _ => (SelfTestState.Failed, null, detail),
        };
    }

    static (SelfTestState, int?, string?) ParseNvmeSelfTest(JsonElement log)
    {
        if ((Long(log, "current_self_test_operation", "value") ?? 0) != 0)
            return (SelfTestState.Running, 100 - (int)(Long(log, "current_self_test_completion_percent") ?? 0), null);
        if (!log.TryGetProperty("table", out var table) || table.ValueKind != JsonValueKind.Array || table.GetArrayLength() == 0)
            return (SelfTestState.None, null, null);
        var latest = table[0];
        var detail = Text(latest, "self_test_result", "string");
        return Long(latest, "self_test_result", "value") switch
        {
            0 => (SelfTestState.Passed, null, detail),
            1 or 2 or 3 or 4 or 8 or 9 => (SelfTestState.Aborted, null, detail),
            null => (SelfTestState.None, null, null),
            _ => (SelfTestState.Failed, null, detail),
        };
    }

    static (SelfTestState, int?, string?) ParseScsiSelfTest(JsonElement latest)
    {
        var detail = Text(latest, "result", "string");
        return Long(latest, "result", "value") switch
        {
            15 => (SelfTestState.Running, null, detail),
            0 => (SelfTestState.Passed, null, detail),
            1 or 2 => (SelfTestState.Aborted, null, detail),
            null => (SelfTestState.None, null, null),
            _ => (SelfTestState.Failed, null, detail),
        };
    }

    /// <summary>Why <c>smartctl -t</c> could not start a self-test; null if it started.</summary>
    public static string? StartError(CommandResult r)
    {
        // Bits 0 to 2: invalid command line, device that cannot be opened, command refused by the disk.
        var failed = r.ExitCode < 0 || (r.ExitCode & 7) != 0;
        if (!failed) return null;
        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && FirstMessage(doc.RootElement) is { } message) return message;
        }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(r.StdErr) ? Msg.F("smartctl ended with code {0}.", r.ExitCode) : r.StdErr.Trim();
    }

    static string? Text(JsonElement e, params string[] path) =>
        Dig(e, path) is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

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
            var r = await SmartctlAsync(["-j", "-a", d.Path], ct);

            // smartctl renvoie un code de sortie par bits, même quand le JSON est complet : seul le JSON compte.
            result.Add(string.IsNullOrWhiteSpace(r.StdOut)
                ? new SmartInfo(d.Path, "unsupported", null, null, null, null, r.StdErr.Trim())
                : SmartParser.Parse(d.Path, r.StdOut));
        }
        return result;
    }

    /// <summary>Self-test state of the disks that have a serial number; only the requested ones when <paramref name="serials"/> is not empty.</summary>
    public async Task<IReadOnlyList<SelfTestStatus>> ListSelfTestsAsync(IReadOnlyCollection<string> serials, CancellationToken ct = default)
    {
        var result = new List<SelfTestStatus>();
        foreach (var d in await disks.ListAsync(ct))
        {
            if (string.IsNullOrEmpty(d.Serial) || (serials.Count > 0 && !serials.Contains(d.Serial))) continue;
            result.Add(await SelfTestAsync(d, ct));
        }
        return result;
    }

    public async Task StartSelfTestAsync(SelfTestRequest request, CancellationToken ct = default)
    {
        if (request.Type is not (SelfTestType.Short or SelfTestType.Long)) throw new OperationException(Msg.F("Unknown self-test type."));
        // The device path always comes from the system's own list, never from the request.
        var found = (await disks.ListAsync(ct)).Where(d => !string.IsNullOrEmpty(d.Serial) && d.Serial == request.Serial).ToList();
        if (found.Count == 0) throw new OperationException(Msg.F("No disk has the serial number {0}.", request.Serial));
        if (found.Count > 1) throw new OperationException(Msg.F("Several disks have the serial number {0}.", request.Serial));

        var status = await SelfTestAsync(found[0], ct);
        if (status.State == SelfTestState.Unsupported) throw new OperationException(Msg.F("This disk does not support self-tests."));
        if (status.State == SelfTestState.Running) throw new OperationException(Msg.F("A self-test is already running."));
        if (SmartParser.StartError(await SmartctlAsync(["-j", "-t", request.Type, found[0].Path], ct)) is { } error)
            throw new OperationException(Msg.F("The self-test could not be started: {0}", error));
    }

    async Task<SelfTestStatus> SelfTestAsync(DiskInfo d, CancellationToken ct)
    {
        var r = await SmartctlAsync(["-j", "-a", d.Path], ct);
        var (state, remaining, detail) = string.IsNullOrWhiteSpace(r.StdOut)
            ? (SelfTestState.Unsupported, null, r.StdErr.Trim())
            : SmartParser.ParseSelfTest(r.StdOut);
        return new SelfTestStatus(d.Path, d.Serial!, d.Model, d.SizeBytes, state, remaining, detail);
    }

    async Task<CommandResult> SmartctlAsync(string[] args, CancellationToken ct)
    {
        try { return await run.RunAsync("smartctl", args, ct: ct); }
        catch (Win32Exception) { throw new OperationException("smartmontools is not installed on the NAS (apt install smartmontools)."); }
    }
}
