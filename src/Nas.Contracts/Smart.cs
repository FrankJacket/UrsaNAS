namespace Nas.Contracts;

/// <summary>
/// État SMART d'un disque. <c>Status</c> : <c>ok</c>, <c>warning</c> (secteurs réalloués ou en attente),
/// <c>failing</c> (le disque annonce sa propre défaillance) ou <c>unsupported</c> (disque virtuel, SMART absent).
/// </summary>
public sealed record SmartInfo(string Disk, string Status, int? TemperatureC, long? PowerOnHours,
    long? ReallocatedSectors, long? PendingSectors, string? Detail);
