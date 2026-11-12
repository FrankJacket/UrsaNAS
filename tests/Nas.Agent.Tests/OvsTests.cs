using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent.Tests;

public class OvsTests
{
    /// <summary>Un système où le paquet est installé ou non, le service actif ou non, avec des ponts OVS éventuels.</summary>
    sealed class Host
    {
        public bool Installed, Active, Apt = true, StartFails;
        public string Bridges = "";

        public CommandResult Handle(FakeRunner.Call c)
        {
            switch (c.File)
            {
                case "dpkg-query": return new CommandResult(Installed ? 0 : 1, Installed ? "install ok installed" : "", "");
                case "apt-get" when c.Args.SequenceEqual(["--version"]): return Apt ? new CommandResult(0, "apt 3.0", "") : throw new System.ComponentModel.Win32Exception("no apt");
                case "env": Installed = true; return new CommandResult(0, "", "");
                case "systemctl" when c.Args[0] == "is-active": return new CommandResult(Active ? 0 : 3, Active ? "active\n" : "inactive\n", "");
                case "systemctl" when c.Args[0] == "enable": if (StartFails) return new CommandResult(1, "", "Job failed\nsee journal"); Active = true; return new CommandResult(0, "", "");
                case "systemctl" when c.Args[0] == "disable": Active = false; return new CommandResult(0, "", "");
                case "ovs-vsctl" when c.Args[0] == "--version": return new CommandResult(0, "ovs-vsctl (Open vSwitch) 3.3.0\nDB Schema 8.5.0\n", "");
                case "ovs-vsctl" when c.Args[0] == "list-br": return new CommandResult(0, Bridges, "");
            }
            return new CommandResult(0, "", "");
        }
    }

    static (OvsService Svc, FakeRunner Runner) Make(Host h)
    {
        var runner = new FakeRunner { Handler = h.Handle };
        var cfg = new ConfigurationBuilder().Build();
        return (new OvsService(runner, cfg, NullLogger<OvsService>.Instance), runner);
    }

    [Fact]
    public async Task NotInstalledIsReportedAsInstallable()
    {
        var (svc, _) = Make(new Host());
        var s = await svc.StatusAsync();
        Assert.False(s.Installed);
        Assert.False(s.Active);
        Assert.True(s.CanInstall);
    }

    [Fact]
    public async Task WithoutAptItCannotBeInstalled()
    {
        var (svc, _) = Make(new Host { Apt = false });
        Assert.False((await svc.StatusAsync()).CanInstall);
        await Assert.ThrowsAsync<OperationException>(() => svc.SetEnabledAsync(true));
    }

    [Fact]
    public async Task EnablingInstallsThePackageThenStartsTheService()
    {
        var host = new Host();
        var (svc, runner) = Make(host);
        await svc.SetEnabledAsync(true);
        var install = Assert.Single(runner.Calls, c => c.File == "env");
        Assert.Contains("openvswitch-switch", install.Args);
        Assert.Contains("DEBIAN_FRONTEND=noninteractive", install.Args);
        Assert.Contains(runner.Calls, c => c.File == "systemctl" && c.Args.SequenceEqual(["enable", "--now", "openvswitch-switch"]));
        var s = await svc.StatusAsync();
        Assert.True(s.Active);
        Assert.Equal("3.3.0", s.Version);
    }

    [Fact]
    public async Task EnablingAnInstalledPackageDoesNotReinstall()
    {
        var (svc, runner) = Make(new Host { Installed = true });
        await svc.SetEnabledAsync(true);
        Assert.DoesNotContain(runner.Calls, c => c.File == "env");
    }

    [Fact]
    public async Task AServiceThatDoesNotStartIsReported()
    {
        var (svc, _) = Make(new Host { Installed = true, StartFails = true });
        var e = await Assert.ThrowsAsync<OperationException>(() => svc.SetEnabledAsync(true));
        Assert.Contains("see journal", e.Message);
    }

    [Fact]
    public async Task DisablingStopsTheServiceAndKeepsThePackage()
    {
        var host = new Host { Installed = true, Active = true };
        var (svc, runner) = Make(host);
        await svc.SetEnabledAsync(false);
        Assert.False(host.Active);
        Assert.True(host.Installed);
        Assert.Contains(runner.Calls, c => c.File == "systemctl" && c.Args.SequenceEqual(["disable", "--now", "openvswitch-switch"]));
    }

    [Fact]
    public async Task DisablingIsRefusedWhileBridgesExist()
    {
        var host = new Host { Installed = true, Active = true, Bridges = "ovsbr0\nbr-int\n" };
        var (svc, _) = Make(host);
        var e = await Assert.ThrowsAsync<OperationException>(() => svc.SetEnabledAsync(false));
        Assert.Contains("ovsbr0, br-int", e.Message);
        Assert.True(host.Active);
    }

    [Fact]
    public async Task DisablingWhenNotInstalledDoesNothing()
    {
        var (svc, runner) = Make(new Host());
        await svc.SetEnabledAsync(false);
        Assert.DoesNotContain(runner.Calls, c => c.File == "systemctl" && c.Args[0] == "disable");
    }

    [Fact]
    public void VersionParsing()
    {
        Assert.Equal("3.3.0", OvsService.ParseVersion("ovs-vsctl (Open vSwitch) 3.3.0\nDB Schema 8.5.0"));
        Assert.Null(OvsService.ParseVersion("garbage"));
    }
}
