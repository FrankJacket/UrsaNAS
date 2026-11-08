using System.Text.RegularExpressions;

namespace Nas.Contracts;

/// <summary>NotInstalled : pas de commande virsh. Stopped : installé mais libvirt ne répond pas. Ready : utilisable.</summary>
public enum VmHostState { NotInstalled, Stopped, Ready }

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
public sealed record CreateVmRequest(string Name, int Vcpus, int MemoryMb, int DiskGb, string IsoLocation, string IsoPath, string? Bridge);

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
        return ValidateBridge(r.Bridge);
    }
}
