using System.ComponentModel;
using System.Globalization;
using System.Xml.Linq;
using Nas.Contracts;

namespace Nas.Agent.Services;

public sealed partial class VmService
{
    // ---- Réglages (définition libvirt)

    string QemuImg => cfg["Vm:QemuImg"] ?? "qemu-img";

    async Task<string> StateAsync(string name, CancellationToken ct) =>
        (await Ok(["domstate", name], "Reading the machine", ct)).Trim();

    async Task<XDocument> DefinitionAsync(string name, CancellationToken ct)
    {
        try { return XDocument.Parse(await Ok(["dumpxml", "--inactive", name], "Reading the machine", ct)); }
        catch (System.Xml.XmlException) { throw new OperationException(Msg.F("Unreadable machine definition.")); }
    }

    /// <summary>Le matériel ne se modifie que machine arrêtée : la définition change au prochain démarrage.</summary>
    async Task RequireStoppedAsync(string name, CancellationToken ct)
    {
        if (Rules.ValidateVmName(name) is { } e) throw new OperationException(e);
        if (await StateAsync(name, ct) != "shut off") throw new OperationException(Msg.F("Shut the machine down to change its hardware."));
    }

    async Task DefineAsync(XDocument doc, CancellationToken ct)
    {
        var file = Path.Combine(Path.GetTempPath(), "ursanas-vm-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            await File.WriteAllTextAsync(file, doc.ToString(), ct);
            await Ok(["define", file], "Saving the machine", ct);
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    long VirtualSizeGb(string file)
    {
        try
        {
            var r = run.RunAsync(QemuImg, ["info", "--output=json", "-U", file]).GetAwaiter().GetResult();
            if (r.ExitCode != 0) return 0;
            using var j = System.Text.Json.JsonDocument.Parse(r.StdOut);
            return j.RootElement.TryGetProperty("virtual-size", out var v) ? v.GetInt64() / (1024L * 1024 * 1024) : 0;
        }
        catch (Exception e) when (e is Win32Exception or System.Text.Json.JsonException or InvalidOperationException) { return 0; }
    }

    public async Task<VmConfig> ConfigAsync(string name, CancellationToken ct = default)
    {
        if (Rules.ValidateVmName(name) is { } e) throw new OperationException(e);
        var info = VmParser.Fields(await Ok(["dominfo", name], "Reading the machine", ct));
        var doc = await DefinitionAsync(name, ct);
        return VmXml.Read(doc.ToString(), info.GetValueOrDefault("State") ?? "unknown", info.GetValueOrDefault("Autostart") == "enable", VirtualSizeGb);
    }

    public Task SaveSettingsAsync(string name, VmSettings s, CancellationToken ct = default)
    {
        if (Rules.ValidateVmName(name) is { } e1) throw new OperationException(e1);
        if (Rules.ValidateVmSettings(s) is { } e2) throw new OperationException(e2);
        if (s.Bios == "ovmf" && !Directory.Exists(cfg["Vm:OvmfDir"] ?? "/usr/share/OVMF")) throw new OperationException(Msg.F("UEFI firmware (OVMF) is not installed on the NAS."));
        return Mutate(async () =>
        {
            await RequireStoppedAsync(name, ct);
            var before = await DefinitionAsync(name, ct);
            var old = VmXml.Read(before.ToString(), "shut off", false);
            await DefineAsync(VmXml.ApplySettings(before, s), ct);
            // Les clés Secure Boot vivent dans le fichier de variables UEFI : il faut le recréer pour que le changement prenne effet.
            if ((old.Bios != s.Bios || old.SecureBoot != s.SecureBoot) && VmXml.NvramPath(before) is { } nvram && nvram.StartsWith("/var/lib/libvirt/qemu/nvram/", StringComparison.Ordinal))
                try { File.Delete(nvram); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            await Ok(s.Autostart ? ["autostart", name] : ["autostart", "--disable", name], "Saving the machine", ct);
        });
    }

    public Task AddDiskAsync(string name, AddVmDiskRequest r, CancellationToken ct = default)
    {
        if (!Rules.VmDiskBuses.Contains(r.Bus)) throw new OperationException(Msg.F("Unknown setting."));
        if (r.SizeGb is < 1 or > Rules.MaxVmDiskGb) throw new OperationException(Msg.F("The disk size must be between 1 and {0} GB.", Rules.MaxVmDiskGb));
        return Mutate(async () =>
        {
            await RequireStoppedAsync(name, ct);
            var doc = await DefinitionAsync(name, ct);
            var file = Path.Combine(DiskDir, $"{name}-{Guid.NewGuid().ToString("N")[..8]}.qcow2");
            Directory.CreateDirectory(DiskDir);
            await Ok(["create", "-f", "qcow2", file, r.SizeGb.ToString(CultureInfo.InvariantCulture) + "G"], "Creating the disk", ct, QemuImg);
            try { await DefineAsync(VmXml.AddDisk(doc, r.Bus, file).Doc, ct); }
            catch { try { File.Delete(file); } catch (IOException) { } throw; }
        });
    }

    public Task RemoveDiskAsync(string name, string target, bool deleteFile, CancellationToken ct = default)
    {
        if (!Rules.IsDiskTarget(target)) throw new OperationException(Msg.F("Unknown disk."));
        return Mutate(async () =>
        {
            await RequireStoppedAsync(name, ct);
            var (doc, file, cdrom) = VmXml.RemoveDisk(await DefinitionAsync(name, ct), target);
            await DefineAsync(doc, ct);
            // Seuls les fichiers créés par nous (dans le dossier des disques) peuvent être supprimés, jamais une image d'un partage.
            if (deleteFile && file is not null && InDiskDir(file) && (!cdrom || file.EndsWith("-install.iso", StringComparison.Ordinal)))
                try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }

    bool InDiskDir(string file) =>
        Path.GetFullPath(file).Replace((char)92, '/').StartsWith(Path.GetFullPath(DiskDir).Replace((char)92, '/') + "/", StringComparison.Ordinal);

    public Task ResizeDiskAsync(string name, ResizeVmDiskRequest r, CancellationToken ct = default)
    {
        if (!Rules.IsDiskTarget(r.Target)) throw new OperationException(Msg.F("Unknown disk."));
        if (r.SizeGb is < 1 or > Rules.MaxVmDiskGb) throw new OperationException(Msg.F("The disk size must be between 1 and {0} GB.", Rules.MaxVmDiskGb));
        return Mutate(async () =>
        {
            await RequireStoppedAsync(name, ct);
            var doc = await DefinitionAsync(name, ct);
            if (!VmXml.IsDisk(doc, r.Target) || VmXml.DiskFile(doc, r.Target) is not { } file) throw new OperationException(Msg.F("Unknown disk."));
            if (r.SizeGb <= VirtualSizeGb(file)) throw new OperationException(Msg.F("A disk can only be made bigger."));
            await Ok(["resize", file, r.SizeGb.ToString(CultureInfo.InvariantCulture) + "G"], "Resizing the disk", ct, QemuImg);
        });
    }

    public Task SetCdromAsync(string name, SetVmCdromRequest r, CancellationToken ct = default)
    {
        if (!Rules.IsDiskTarget(r.Target)) throw new OperationException(Msg.F("Unknown disk."));
        var eject = string.IsNullOrEmpty(r.IsoLocation);
        if (!eject && (string.IsNullOrEmpty(r.IsoPath) || !r.IsoPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || AclRules.ValidateFolderPath(r.IsoPath) is not null))
            throw new OperationException(Msg.F("The installation image must be an .iso file."));
        return Mutate(async () =>
        {
            await RequireStoppedAsync(name, ct);
            string? copy = null;
            if (!eject)
            {
                var iso = await IsoFullPathAsync(r.IsoLocation!, r.IsoPath!, ct);
                Directory.CreateDirectory(DiskDir);
                copy = IsoCopy(name);
                try { File.Copy(iso, copy, overwrite: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { throw new OperationException(Msg.F("{0} failed: {1}", "Copying the installation image", ex.Message)); }
            }
            await DefineAsync(VmXml.SetCdrom(await DefinitionAsync(name, ct), r.Target, copy), ct);
        });
    }

    static void CheckNic(string model, string? bridge)
    {
        if (!Rules.VmNicModels.Contains(model) || Rules.ValidateBridge(bridge) is not null) throw new OperationException(Msg.F("Unknown setting."));
    }

    public Task AddNicAsync(string name, AddVmNicRequest r, CancellationToken ct = default)
    {
        CheckNic(r.Model, r.Bridge);
        return Mutate(async () =>
        {
            await RequireStoppedAsync(name, ct);
            await DefineAsync(VmXml.AddNic(await DefinitionAsync(name, ct), r.Model, r.Bridge), ct);
        });
    }

    public Task UpdateNicAsync(string name, UpdateVmNicRequest r, CancellationToken ct = default)
    {
        CheckNic(r.Model, r.Bridge);
        if (!Rules.IsMac(r.Mac)) throw new OperationException(Msg.F("Unknown network card."));
        return Mutate(async () =>
        {
            await RequireStoppedAsync(name, ct);
            await DefineAsync(VmXml.UpdateNic(await DefinitionAsync(name, ct), r.Mac, r.Model, r.Bridge), ct);
        });
    }

    public Task RemoveNicAsync(string name, string mac, CancellationToken ct = default)
    {
        if (!Rules.IsMac(mac)) throw new OperationException(Msg.F("Unknown network card."));
        return Mutate(async () =>
        {
            await RequireStoppedAsync(name, ct);
            await DefineAsync(VmXml.RemoveNic(await DefinitionAsync(name, ct), mac), ct);
        });
    }

    // ---- Snapshots

    public async Task<IReadOnlyList<VmSnapshot>> SnapshotsAsync(string name, CancellationToken ct = default)
    {
        if (Rules.ValidateVmName(name) is { } e) throw new OperationException(e);
        var names = (await Ok(["snapshot-list", name, "--name"], "Listing snapshots", ct)).Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(n => Rules.ValidateVmSnapshotName(n) is null);
        var list = new List<VmSnapshot>();
        foreach (var n in names)
        {
            var f = VmParser.Fields(await Ok(["snapshot-info", name, n], "Listing snapshots", ct));
            var parent = f.GetValueOrDefault("Parent");
            list.Add(new VmSnapshot(n, VmParser.SnapshotCreated(await Ok(["snapshot-dumpxml", name, n], "Listing snapshots", ct)), parent is null or "-" ? null : parent, f.GetValueOrDefault("Current") == "yes"));
        }
        return list;
    }

    public Task CreateSnapshotAsync(string name, CreateVmSnapshotRequest r, CancellationToken ct = default)
    {
        if (Rules.ValidateVmName(name) is { } e1) throw new OperationException(e1);
        if (Rules.ValidateVmSnapshotName(r.Name) is { } e2) throw new OperationException(e2);
        var desc = (r.Description ?? "").Trim();
        if (desc.Length > 200 || desc.Any(char.IsControl)) throw new OperationException(Msg.F("Invalid description (500 characters at most)."));
        return Mutate(async () =>
        {
            // Les variables UEFI de Debian sont au format brut : libvirt ne sait pas les inclure dans un snapshot de machine en marche.
            var current = await ConfigAsync(name, ct);
            if (current.State != "shut off" && current.Bios == "ovmf") throw new OperationException(Msg.F("A machine started with UEFI can only be snapshotted while shut down."));
            if ((await SnapshotsAsync(name, ct)).Count >= Rules.MaxVmSnapshots) throw new OperationException(Msg.F("Too many snapshots."));
            List<string> args = ["snapshot-create-as", name, "--name", r.Name];
            if (desc.Length > 0) args.AddRange(["--description", desc]);
            await Ok(args, "Creating the snapshot", ct);
        });
    }

    public Task SnapshotActionAsync(string name, string snapshot, string action, CancellationToken ct = default)
    {
        if (Rules.ValidateVmName(name) is { } e1) throw new OperationException(e1);
        if (Rules.ValidateVmSnapshotName(snapshot) is { } e2) throw new OperationException(e2);
        return Mutate(async () =>
        {
            switch (action)
            {
                case "revert": await Ok(["snapshot-revert", name, snapshot], "Restoring the snapshot", ct); break;
                case "delete": await Ok(["snapshot-delete", name, snapshot], "Deleting the snapshot", ct); break;
                default: throw new OperationException(Msg.F("Unknown action."));
            }
        });
    }
}
