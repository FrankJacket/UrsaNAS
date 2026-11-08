namespace Nas.Contracts;

/// <summary>Une partition d'un disque. <c>FsType</c> : ext4, xfs, vfat, swap, zfs_member… (null si aucune signature).</summary>
public sealed record PartitionInfo(string Name, string Path, long SizeBytes, string? FsType, string? Label, string? Uuid, IReadOnlyList<string> MountPoints, string? Busy = null);

/// <summary>
/// Un disque physique tel que vu par l'hôte. <c>Busy</c> : pourquoi on n'y touche pas (système, monté, pool ZFS actif, swap) ;
/// null = libre, donc partitionnable, formatable ou effaçable. <c>UnallocatedBytes</c> : espace après la dernière partition.
/// </summary>
public sealed record DiskInfo(
    string Name,
    string Path,
    long SizeBytes,
    string? Model,
    string? Serial,
    bool? Rotational,
    IReadOnlyList<string> MountPoints,
    IReadOnlyList<PartitionInfo>? Partitions = null,
    string? Busy = null,
    long UnallocatedBytes = 0);
