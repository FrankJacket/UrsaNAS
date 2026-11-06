using Nas.Agent.Services;
using Nas.Contracts;
using Xunit;

namespace Nas.Agent.Tests;

public class StatsTests
{
    [Fact]
    public void Le_cpu_compte_idle_et_iowait_comme_inactivite()
    {
        var (total, idle) = StatsParser.ParseCpu("cpu  100 0 50 800 50 0 0 0 0 0\ncpu0 1 2 3 4 5\nintr 1");
        Assert.Equal(1000, total);
        Assert.Equal(850, idle);
    }

    [Fact]
    public void La_memoire_utilise_MemAvailable_sinon_une_estimation()
    {
        var (total, available) = StatsParser.ParseMemory("MemTotal: 16000 kB\nMemFree: 1000 kB\nMemAvailable: 9000 kB\n");
        Assert.Equal(16000 * 1024, total);
        Assert.Equal(9000 * 1024, available);

        var (_, estimated) = StatsParser.ParseMemory("MemTotal: 100 kB\nMemFree: 10 kB\nBuffers: 5 kB\nCached: 20 kB\n");
        Assert.Equal(35 * 1024, estimated);
    }

    [Fact]
    public void Le_reseau_ignore_loopback_docker_et_veth()
    {
        const string text = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
                lo: 999 1 0 0 0 0 0 0 999 1 0 0 0 0 0 0
              eth0: 1000 10 0 0 0 0 0 0 400 5 0 0 0 0 0 0
            docker0: 777 1 0 0 0 0 0 0 777 1 0 0 0 0 0 0
              eth1: 500 10 0 0 0 0 0 0 100 5 0 0 0 0 0 0
            """;
        Assert.Equal((1500, 500), StatsParser.ParseNet(text));
    }

    [Fact]
    public void Les_disques_gardent_les_disques_entiers_et_convertissent_les_secteurs()
    {
        const string text = """
               8       0 sda 100 0 2000 10 50 0 4000 20 0 300 30
               8       1 sda1 90 0 1900 10 40 0 3900 20 0 250 30
               7       0 loop0 1 0 8 0 0 0 0 0 0 0 0
             259       0 nvme0n1 5 0 16 1 6 0 32 2 0 7 3
            """;
        var disks = StatsParser.ParseDisks(text);
        Assert.Equal(["sda", "nvme0n1"], disks.Select(d => d.Name));
        Assert.Equal(new DiskIo("sda", 2000 * 512, 4000 * 512, 300), disks[0]);
    }

    [Theory]
    [InlineData("45000\n", 45.0)]
    [InlineData("0", null)]
    [InlineData("abc", null)]
    [InlineData("900000", null)]
    public void La_temperature_vient_de_millidegres(string text, double? expected) =>
        Assert.Equal(expected, StatsParser.ParseMilliDegrees(text));

    static SystemStats Sample(long ms, long cpuTotal, long cpuIdle, long rx, long tx, params DiskIo[] disks) =>
        new("h", 1, cpuTotal, cpuIdle, null, 100, 50, rx, tx, disks, ms);

    [Fact]
    public void Les_debits_se_calculent_entre_deux_instantanes()
    {
        var a = Sample(0, 1000, 900, 0, 0, new DiskIo("sda", 0, 0, 0));
        var b = Sample(2000, 1400, 1100, 4000, 1000, new DiskIo("sda", 2000, 6000, 1000));
        var r = StatsMath.Rates(a, b)!;
        Assert.Equal(50, r.CpuPercent, 3);          // 200 inactifs sur 400
        Assert.Equal(2000, r.NetRxPerSec);
        Assert.Equal(500, r.NetTxPerSec);
        Assert.Equal(1000, r.Disks["sda"].ReadPerSec);
        Assert.Equal(3000, r.Disks["sda"].WritePerSec);
        Assert.Equal(50, r.Disks["sda"].BusyPercent); // 1000 ms occupées sur 2000 ms
    }

    [Fact]
    public void Pas_de_debit_sans_instantane_precedent_ni_apres_un_redemarrage()
    {
        var b = Sample(2000, 1400, 1100, 0, 0);
        Assert.Null(StatsMath.Rates(null, b));
        Assert.Null(StatsMath.Rates(Sample(0, 9999, 0, 0, 0), b)); // compteurs repartis de zéro
        Assert.Null(StatsMath.Rates(Sample(2000, 1, 0, 0, 0), b)); // même instant
    }
}
