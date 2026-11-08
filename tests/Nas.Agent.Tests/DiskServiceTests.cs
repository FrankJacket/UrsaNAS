using Microsoft.Extensions.Configuration;
using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent.Tests;

public class DiskServiceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-disk-" + Guid.NewGuid().ToString("N"));
    readonly FakeRunner _run = new();
    readonly ListProvider _disks = new();
    readonly DiskService _svc;
    readonly string _fstab, _root;

    sealed class ListProvider : IDiskProvider
    {
        public List<DiskInfo> Disks { get; } = [];
        public Task<IReadOnlyList<DiskInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DiskInfo>>(Disks);
    }

    static DiskInfo Free(string path, params PartitionInfo[] parts) =>
        new(Path.GetFileName(path), path, 10L << 30, "Virtual Disk", "S1", false, [], parts, null, 10L << 30);

    public DiskServiceTests()
    {
        var bin = Path.Combine(_dir, "bin");
        _root = Path.Combine(_dir, "mnt").Replace((char)92, '/');
        _fstab = Path.Combine(_dir, "fstab");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "mkfs.ext4"), "x");
        File.WriteAllText(Path.Combine(bin, "mkfs.exfat"), "x");
        File.WriteAllText(_fstab, "UUID=abc / ext4 defaults 0 1\n");
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Disks:Fstab"] = _fstab, ["Disks:MountRoot"] = _root, ["Disks:BinDirs"] = bin, ["Disks:NoWait"] = "true",
        }).Build();
        _svc = new DiskService(_run, _disks, cfg, new ZfsService(_run, _disks, cfg));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Only_the_installed_mkfs_tools_are_offered() =>
        Assert.Equal(["ext4", "exfat"], _svc.Filesystems().Available);

    [Fact]
    public async Task Prepare_refuses_a_busy_disk()
    {
        _disks.Disks.Add(Free("/dev/sdb") with { Busy = "This disk belongs to a ZFS pool." });
        await Assert.ThrowsAsync<OperationException>(() => _svc.PrepareDiskAsync(new("/dev/sdb", "ext4", null)));
        Assert.Empty(_run.Calls);
    }

    [Fact]
    public async Task Prepare_rejects_unknown_disk_and_bad_paths()
    {
        _disks.Disks.Add(Free("/dev/sdb"));
        await Assert.ThrowsAsync<OperationException>(() => _svc.PrepareDiskAsync(new("/dev/sdz", "ext4", null)));
        await Assert.ThrowsAsync<OperationException>(() => _svc.PrepareDiskAsync(new("/etc/passwd", "ext4", null)));
        await Assert.ThrowsAsync<OperationException>(() => _svc.PrepareDiskAsync(new("/dev/sdb", "btrfs", null)));
        await Assert.ThrowsAsync<OperationException>(() => _svc.PrepareDiskAsync(new("/dev/sdb", "ext4", "bad label!")));
        Assert.Empty(_run.Calls);
    }

    [Fact]
    public async Task Prepare_refuses_a_file_system_whose_tool_is_missing()
    {
        _disks.Disks.Add(Free("/dev/sdb"));
        await Assert.ThrowsAsync<OperationException>(() => _svc.PrepareDiskAsync(new("/dev/sdb", "btrfs", null)));
        Assert.DoesNotContain(_run.Calls, c => c.File == "sfdisk");
    }

    [Fact]
    public async Task Prepare_wipes_then_creates_one_gpt_partition_then_formats()
    {
        _disks.Disks.Add(Free("/dev/sdb"));
        await _svc.PrepareDiskAsync(new("/dev/sdb", "ext4", "data"));
        Assert.Contains(_run.Calls, c => c.File == "mkfs.ext4" && c.Args.Contains("/dev/sdb1") && c.Args.Contains("data"));
        var files = _run.Calls.Select(c => c.File).ToList();
        Assert.True(files.IndexOf("wipefs") < files.IndexOf("sfdisk"));
        var sfdisk = _run.Calls.Single(c => c.File == "sfdisk");
        Assert.Contains("label: gpt", sfdisk.Stdin);
        Assert.Contains("/dev/sdb", sfdisk.Args);
    }

    [Fact]
    public async Task CreatePartition_appends_in_free_space_even_on_the_system_disk()
    {
        var parts = new[]
        {
            new PartitionInfo("sda1", "/dev/sda1", 1L << 30, "vfat", null, "u1", ["/boot/efi"]),
            new PartitionInfo("sda2", "/dev/sda2", 5L << 30, "ext4", null, "u2", ["/"]),
        };
        _disks.Disks.Add(Free("/dev/sda", parts) with { Busy = "The system is on this disk.", UnallocatedBytes = 4L << 30 });
        await _svc.CreatePartitionAsync(new("/dev/sda", 2048, "ext4", "data"));
        var sfdisk = _run.Calls.Single(c => c.File == "sfdisk");
        Assert.Contains("--append", sfdisk.Args);
        Assert.Equal(",2048M,L\n", sfdisk.Stdin);
        Assert.DoesNotContain(_run.Calls, c => c.File == "wipefs");
        Assert.Contains(_run.Calls, c => c.File == "mkfs.ext4" && c.Args.Contains("/dev/sda3"));
    }

    [Fact]
    public async Task CreatePartition_refuses_more_than_the_free_space()
    {
        _disks.Disks.Add(Free("/dev/sdb", new PartitionInfo("sdb1", "/dev/sdb1", 1L << 30, "ext4", null, "u", [])) with { UnallocatedBytes = 1L << 30 });
        await Assert.ThrowsAsync<OperationException>(() => _svc.CreatePartitionAsync(new("/dev/sdb", 4096, "ext4", null)));
        Assert.Empty(_run.Calls);
    }

    [Fact]
    public async Task Zfs_needs_a_pool_name_and_creates_a_single_device_pool()
    {
        File.WriteAllText(Path.Combine(_dir, "bin", "zpool"), "x");
        var p = new PartitionInfo("sdb1", "/dev/sdb1", 5L << 30, null, null, null, []);
        _disks.Disks.Add(Free("/dev/sdb", p));
        await Assert.ThrowsAsync<OperationException>(() => _svc.FormatPartitionAsync(new("/dev/sdb1", "zfs", null)));
        await _svc.FormatPartitionAsync(new("/dev/sdb1", "zfs", "media"));
        var create = _run.Calls.Single(c => c.File == "zpool" && c.Args[0] == "create");
        Assert.Equal(["media", "/dev/sdb1"], create.Args[^2..]);
        Assert.DoesNotContain(_run.Calls, c => c.File.StartsWith("mkfs"));
    }

    [Fact]
    public async Task Wipe_refuses_the_system_disk()
    {
        _disks.Disks.Add(Free("/dev/sda") with { Busy = "The system is on this disk." });
        await Assert.ThrowsAsync<OperationException>(() => _svc.WipeDiskAsync("/dev/sda"));
        Assert.Empty(_run.Calls);
    }

    [Fact]
    public async Task Wipe_erases_partitions_and_disk_signatures()
    {
        _disks.Disks.Add(Free("/dev/sdb", new PartitionInfo("sdb1", "/dev/sdb1", 5L << 30, "ext4", null, "u", [])));
        await _svc.WipeDiskAsync("/dev/sdb");
        Assert.Contains(_run.Calls, c => c.File == "wipefs" && c.Args.Contains("/dev/sdb1"));
        Assert.Contains(_run.Calls, c => c.File == "wipefs" && c.Args.Contains("/dev/sdb"));
    }

    [Fact]
    public async Task Format_refuses_a_mounted_partition()
    {
        var p = new PartitionInfo("sdb1", "/dev/sdb1", 5L << 30, "ext4", null, "u", ["/mnt/x"]);
        _disks.Disks.Add(Free("/dev/sdb", p) with { Busy = "A partition is mounted: /mnt/x." });
        await Assert.ThrowsAsync<OperationException>(() => _svc.FormatPartitionAsync(new("/dev/sdb1", "ext4", null)));
        Assert.Empty(_run.Calls);
    }

    [Fact]
    public async Task Mount_creates_the_folder_and_a_tagged_fstab_line_and_unmount_removes_it()
    {
        var p = new PartitionInfo("sdb1", "/dev/sdb1", 5L << 30, "ext4", "data", "1234-uuid", []);
        _disks.Disks.Add(Free("/dev/sdb", p));
        await _svc.MountAsync(new("/dev/sdb1", "data", true));

        var target = _root + "/data";
        Assert.True(Directory.Exists(target));
        Assert.Contains(_run.Calls, c => c.File == "mount" && c.Args.Contains("/dev/sdb1") && c.Args.Contains(target));
        var fstab = File.ReadAllText(_fstab);
        Assert.Contains("UUID=1234-uuid " + target + " ext4 defaults,nofail 0 2 # ursanas", fstab);
        Assert.Contains("UUID=abc / ext4", fstab); // les autres lignes restent intactes
        Assert.True(File.Exists(_fstab + ".ursanas-bak"));

        // Le disque est maintenant monté : on peut le démonter.
        _disks.Disks[0] = Free("/dev/sdb", p with { MountPoints = [target] }) with { MountPoints = [target], Busy = "A partition is mounted." };
        await _svc.UnmountAsync(target);
        Assert.Contains(_run.Calls, c => c.File == "umount" && c.Args.Contains(target));
        Assert.DoesNotContain("# ursanas", File.ReadAllText(_fstab));
        Assert.Contains("UUID=abc / ext4", File.ReadAllText(_fstab));
    }

    [Fact]
    public async Task Mount_without_persistence_leaves_fstab_alone()
    {
        _disks.Disks.Add(Free("/dev/sdb", new PartitionInfo("sdb1", "/dev/sdb1", 5L << 30, "ext4", null, "u", [])));
        await _svc.MountAsync(new("/dev/sdb1", "tmp", false));
        Assert.Equal("UUID=abc / ext4 defaults 0 1\n", File.ReadAllText(_fstab));
    }

    [Fact]
    public async Task Mount_refuses_swap_and_zfs_partitions()
    {
        _disks.Disks.Add(Free("/dev/sdb",
            new PartitionInfo("sdb1", "/dev/sdb1", 1L << 30, "swap", null, "u1", []),
            new PartitionInfo("sdb2", "/dev/sdb2", 1L << 30, "zfs_member", null, "u2", [])));
        await Assert.ThrowsAsync<OperationException>(() => _svc.MountAsync(new("/dev/sdb1", "a", false)));
        await Assert.ThrowsAsync<OperationException>(() => _svc.MountAsync(new("/dev/sdb2", "b", false)));
        Assert.DoesNotContain(_run.Calls, c => c.File == "mount");
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("/")]
    [InlineData("/srv/../mnt/x")]
    public async Task Unmount_only_accepts_folders_under_the_mount_root(string path) =>
        await Assert.ThrowsAsync<OperationException>(() => _svc.UnmountAsync(path));

    [Fact]
    public void Sfdisk_json_is_parsed()
    {
        const string json = """
            {"partitiontable":{"label":"gpt","device":"/dev/sda","unit":"sectors","firstlba":34,"lastlba":83886046,
             "partitions":[{"node":"/dev/sda1","start":2048,"size":1048576,"type":"C12A7328"},{"node":"/dev/sda2","start":1050624,"size":40000000,"type":"0FC63DAF"}]}}
            """;
        var (parts, last) = DiskService.ParseTable(json);
        Assert.Equal(83886046, last);
        Assert.Equal(2, parts.Count);
        Assert.Equal("/dev/sda2", parts[1].Node);
        Assert.Equal(1050624, parts[1].Start);
    }
}

public class DiskShareRulesTests
{
    static readonly IReadOnlySet<string> Users = new HashSet<string> { "bob" };
    static ShareDef Share(string name, string? disk, string? dataset = null) => new(name, "", false, ["bob"], dataset, false, null, null, disk);

    [Fact] public void A_mounted_disk_can_be_a_share() => Assert.Null(Rules.ValidateShares([Share("media", "/mnt/data")], Users));

    [Theory]
    [InlineData("/etc")]
    [InlineData("/mnt/../etc")]
    [InlineData("/mnt/")]
    [InlineData("/srv/nas/x")]
    public void Other_folders_are_refused(string dir) => Assert.NotNull(Rules.ValidateShares([Share("media", dir)], Users));

    [Fact] public void A_disk_cannot_serve_two_shares() => Assert.NotNull(Rules.ValidateShares([Share("a", "/mnt/data"), Share("b", "/mnt/data")], Users));

    [Fact] public void A_share_cannot_have_a_dataset_and_a_disk() => Assert.NotNull(Rules.ValidateShares([Share("a", "/mnt/data", "tank/a")], Users));

    [Fact]
    public void The_samba_path_is_the_mount_folder()
    {
        var conf = Nas.Agent.Services.ShareConf.Render([Share("media", "/mnt/data")], "/srv/nas");
        Assert.Contains("path = /mnt/data", conf);
    }
}
