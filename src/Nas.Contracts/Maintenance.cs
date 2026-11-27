namespace Nas.Contracts;

/// <summary>TRIM state of one pool device, as shown by <c>zpool status -t</c>. A cancelled TRIM shows as <see cref="Untrimmed"/> again.</summary>
public enum TrimState { Untrimmed, Running, Suspended, Completed, Unsupported }

/// <param name="Online">The device is in service (ONLINE or DEGRADED): ZFS can only trim those.</param>
/// <param name="Percent">How much is trimmed, when ZFS shows it (running, paused or completed).</param>
public sealed record TrimDevice(string Name, bool Online, TrimState State, int? Percent = null);

/// <summary>The data and log devices of a pool; spares and cache devices are left out, ZFS does not trim them with the pool.</summary>
public sealed record PoolTrimStatus(string Pool, IReadOnlyList<TrimDevice> Devices);

public static class SelfTestType
{
    public const string Short = "short", Long = "long";
}

/// <summary>
/// <see cref="None"/>: no result to show (a SCSI disk never tested). Otherwise the disk's latest self-test, or the one in progress.
/// </summary>
public enum SelfTestState { Unsupported, None, Running, Passed, Failed, Aborted }

/// <summary>A disk is identified by its serial number: its name (/dev/sdX) can change when the NAS restarts.</summary>
/// <param name="Detail">What the disk or smartctl says about the result, in English.</param>
public sealed record SelfTestStatus(string Disk, string Serial, string? Model, long SizeBytes, SelfTestState State, int? RemainingPercent, string? Detail);

public sealed record SelfTestRequest(string Serial, string Type);
