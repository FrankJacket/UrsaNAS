using System.Text.RegularExpressions;

namespace Nas.Contracts;

/// <summary>NotInstalled : pas de commande virsh. Stopped : installé mais libvirt ne répond pas. Ready : utilisable.</summary>
/// <remarks>Unsupported: virtual machines are switched off on this processor architecture (only x86-64 is handled).</remarks>
public enum VmHostState { NotInstalled, Stopped, Ready, Unsupported }

/// <summary><c>Kvm</c> : /dev/kvm existe (sinon QEMU émule, très lentement). <c>Bridges</c> : ponts réseau de la machine, utilisables par les VM.</summary>
public sealed record VmStatus(VmHostState State, string? Version, bool Kvm, IReadOnlyList<string> Bridges);

/// <summary><c>State</c> : running, shut off, paused, crashed… <c>VncPort</c> : port d'écran de la VM en marche, sinon null.</summary>
public sealed record VmInfo(string Name, string State, int Vcpus, long MemoryMb, long DiskGb, int? VncPort);

/// <summary>Un fichier .iso trouvé dans un partage ou sur un disque monté.</summary>
public sealed record IsoFile(string Location, string Path, long SizeBytes);

/// <summary>
/// <c>IsoLocation</c>/<c>IsoPath</c> : partage (ou disque monté) et chemin du fichier .iso à brancher. <c>Bridge</c> : pont réseau de la
/// machine ; vide = réseau « default » de libvirt (NAT).
/// </summary>
public sealed record CreateVmRequest(string Name, int Vcpus, int MemoryMb, int DiskGb, string IsoLocation, string IsoPath, string? Bridge, string Bios = "", bool SecureBoot = false, string? StorageDir = null);

public sealed record VmPort(int Port);

public static partial class Rules
{
    public const int MaxVmCpus = 64, MaxVmMemoryMb = 1024 * 1024, MaxVmDiskGb = 16 * 1024;
    public static readonly IReadOnlyList<string> VmActions = ["start", "shutdown", "stop", "remove"];

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,39}$")]
    private static partial Regex VmNameRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,14}$")]
    private static partial Regex BridgeRegex();

    public static string? ValidateVmName(string? name) =>
        string.IsNullOrEmpty(name) || !VmNameRegex().IsMatch(name)
            ? Msg.F("Invalid machine name: 1 to 40 characters (letters, digits, _ . and -), starting with a letter or a digit.")
            : null;

    public static string? ValidateBridge(string? bridge) =>
        string.IsNullOrEmpty(bridge) || BridgeRegex().IsMatch(bridge) ? null : Msg.F("Invalid network bridge name.");

    public static string? ValidateCreateVm(CreateVmRequest r)
    {
        if (ValidateVmName(r.Name) is { } e1) return e1;
        if (r.Vcpus is < 1 or > MaxVmCpus) return Msg.F("The number of processors must be between 1 and {0}.", MaxVmCpus);
        if (r.MemoryMb is < 64 or > MaxVmMemoryMb) return Msg.F("The memory must be between 64 MB and {0} MB.", MaxVmMemoryMb);
        if (r.DiskGb is < 1 or > MaxVmDiskGb) return Msg.F("The disk size must be between 1 and {0} GB.", MaxVmDiskGb);
        if (string.IsNullOrEmpty(r.IsoLocation) || string.IsNullOrEmpty(r.IsoPath)) return Msg.F("Choose an installation image (.iso).");
        if (!r.IsoPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) return Msg.F("The installation image must be an .iso file.");
        if (AclRules.ValidateFolderPath(r.IsoPath) is { } e2) return e2;
        if (r.Bios is not ("" or "seabios" or "ovmf") || (r.SecureBoot && r.Bios == "seabios")) return Msg.F("Unknown setting.");
        if (r.StorageDir is not null && ValidateShareDisk(r.StorageDir) is { } e3) return e3;
        return ValidateBridge(r.Bridge);
    }
}

// ---- Réglages d'une machine (à la manière de Proxmox) : lus et écrits dans la définition libvirt, machine arrêtée.

/// <summary><c>Target</c> : vda, sda… <c>Source</c> : fichier du disque ou de l'image ; null = lecteur vide. <c>SizeGb</c> : taille virtuelle (0 pour un lecteur).</summary>
public sealed record VmDisk(string Target, string Bus, string? Source, long SizeGb, bool Cdrom);

/// <summary><c>Bridge</c> : pont de la machine ; vide = réseau « default » de libvirt (NAT).</summary>
public sealed record VmNic(string Mac, string Model, string? Bridge);

/// <summary>
/// Tout ce que la page d'une machine montre. <c>BootOrder</c> : disques (cible) et cartes réseau (adresse MAC) dans l'ordre de démarrage.
/// <c>Bios</c> : seabios ou ovmf (UEFI). <c>Machine</c> : q35 ou pc. <c>CpuType</c> : voir <see cref="Rules.VmCpuTypes"/>.
/// </summary>
public sealed record VmConfig(string Name, string State, string Description, int Sockets, int Cores, string CpuType, int MemoryMb,
    string Bios, string Machine, bool SecureBoot, bool Tpm, bool Autostart, IReadOnlyList<string> BootOrder, IReadOnlyList<VmDisk> Disks, IReadOnlyList<VmNic> Nics);

/// <summary>Réglages généraux modifiables ; la machine doit être arrêtée.</summary>
public sealed record VmSettings(string Description, int Sockets, int Cores, string CpuType, int MemoryMb, string Bios, string Machine,
    bool SecureBoot, bool Tpm, bool Autostart, IReadOnlyList<string> BootOrder);

public sealed record AddVmDiskRequest(string Bus, int SizeGb);
public sealed record ResizeVmDiskRequest(string Target, int SizeGb);
/// <summary>Branche une image au lecteur ; <c>IsoLocation</c> vide = éjecter.</summary>
public sealed record SetVmCdromRequest(string Target, string? IsoLocation, string? IsoPath);
public sealed record AddVmNicRequest(string Model, string? Bridge);
public sealed record UpdateVmNicRequest(string Mac, string Model, string? Bridge);
public sealed record VmSnapshot(string Name, string Created, string? Parent, bool Current);
public sealed record CreateVmSnapshotRequest(string Name, string? Description);

public static partial class Rules
{
    public static readonly IReadOnlyList<string> VmCpuTypes = ["host-passthrough", "host-model", "max", "qemu64"];
    public static readonly IReadOnlyList<string> VmBios = ["seabios", "ovmf"];
    public static readonly IReadOnlyList<string> VmMachines = ["q35", "pc"];
    public static readonly IReadOnlyList<string> VmDiskBuses = ["virtio", "sata", "scsi"];
    public static readonly IReadOnlyList<string> VmNicModels = ["virtio", "e1000", "e1000e", "rtl8139"];
    public const int MaxVmSnapshots = 64;

    [GeneratedRegex("^([0-9a-f]{2}:){5}[0-9a-f]{2}$")]
    private static partial Regex MacRegex();

    [GeneratedRegex("^(vd|sd|hd)[a-z]{1,2}$")]
    private static partial Regex DiskTargetRegex();

    public static bool IsMac(string? s) => s is not null && MacRegex().IsMatch(s);
    public static bool IsDiskTarget(string? s) => s is not null && DiskTargetRegex().IsMatch(s);

    public static string? ValidateVmSettings(VmSettings s)
    {
        if (s.Sockets is < 1 or > 8) return Msg.F("The number of sockets must be between 1 and {0}.", 8);
        if (s.Cores is < 1 or > MaxVmCpus || s.Sockets * s.Cores > MaxVmCpus) return Msg.F("The number of processors must be between 1 and {0}.", MaxVmCpus);
        if (s.MemoryMb is < 64 or > MaxVmMemoryMb) return Msg.F("The memory must be between 64 MB and {0} MB.", MaxVmMemoryMb);
        if (!VmCpuTypes.Contains(s.CpuType) || !VmBios.Contains(s.Bios) || !VmMachines.Contains(s.Machine)) return Msg.F("Unknown setting.");
        if ((s.Description?.Length ?? 0) > 500 || (s.Description ?? "").Any(c => char.IsControl(c) && c != '\n')) return Msg.F("Invalid description (500 characters at most).");
        if (s.SecureBoot && (s.Bios != "ovmf" || s.Machine != "q35")) return Msg.F("Secure Boot needs UEFI (OVMF) and the q35 machine type.");
        foreach (var b in s.BootOrder ?? []) if (!IsDiskTarget(b) && !IsMac(b)) return Msg.F("Unknown setting.");
        return null;
    }

    public static string? ValidateVmSnapshotName(string? name) =>
        string.IsNullOrEmpty(name) || !VmNameRegex().IsMatch(name)
            ? Msg.F("Invalid snapshot name: 1 to 40 characters (letters, digits, _ . and -), starting with a letter or a digit.")
            : null;
}
