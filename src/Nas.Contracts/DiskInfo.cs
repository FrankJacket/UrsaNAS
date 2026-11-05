namespace Nas.Contracts;

/// <summary>Un disque physique tel que vu par l'hôte.</summary>
public sealed record DiskInfo(
    string Name,
    string Path,
    long SizeBytes,
    string? Model,
    string? Serial,
    bool? Rotational,
    IReadOnlyList<string> MountPoints);
