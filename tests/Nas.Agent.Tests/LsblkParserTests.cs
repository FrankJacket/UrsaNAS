using Nas.Agent.Services;
using Xunit;

namespace Nas.Agent.Tests;

public class LsblkParserTests
{
    private const string Sample = """
    {
      "blockdevices": [
        { "name": "sda", "path": "/dev/sda", "size": 21474836480, "type": "disk", "model": "Virtual Disk  ", "serial": "S1", "rota": false, "mountpoints": [null],
          "children": [
            { "name": "sda1", "path": "/dev/sda1", "size": 1, "type": "part", "mountpoints": ["/boot/efi"] },
            { "name": "sda2", "path": "/dev/sda2", "size": 2, "type": "part", "mountpoints": ["/"] }
          ] },
        { "name": "sdb", "path": "/dev/sdb", "size": "10737418240", "type": "disk", "model": null, "serial": null, "rota": "1", "mountpoints": [null] },
        { "name": "sr0", "path": "/dev/sr0", "size": 1073741312, "type": "rom", "mountpoints": [null] },
        { "name": "loop0", "path": "/dev/loop0", "size": 1, "type": "loop", "mountpoints": [null] }
      ]
    }
    """;

    [Fact]
    public void Garde_seulement_les_disques()
    {
        var disks = LsblkParser.Parse(Sample);
        Assert.Equal(["sda", "sdb"], disks.Select(d => d.Name));
    }

    [Fact]
    public void Remonte_les_points_de_montage_des_partitions()
    {
        var sda = LsblkParser.Parse(Sample)[0];
        Assert.Equal(["/boot/efi", "/"], sda.MountPoints);
        Assert.Equal("Virtual Disk", sda.Model);
        Assert.False(sda.Rotational);
    }

    [Fact]
    public void Tolere_les_formats_de_lsblk_anciens()
    {
        var sdb = LsblkParser.Parse(Sample)[1];
        Assert.Equal(10L << 30, sdb.SizeBytes);
        Assert.True(sdb.Rotational);
        Assert.Null(sdb.Model);
        Assert.Empty(sdb.MountPoints);
    }

    [Fact]
    public void Json_sans_blockdevices_donne_une_liste_vide() =>
        Assert.Empty(LsblkParser.Parse("{}"));
}
