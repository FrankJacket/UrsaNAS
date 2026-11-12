using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nas.Agent.Services;

namespace Nas.Agent.Tests;

public class PowerTests
{
    static (PowerService Svc, FakeRunner Runner) Make(bool fake)
    {
        var runner = new FakeRunner { Handler = _ => new CommandResult(0, "", "") };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Power:DelayMs"] = "10", ["URSANAS_FAKE"] = fake ? "1" : "0" }).Build();
        return (new PowerService(runner, cfg, NullLogger<PowerService>.Instance), runner);
    }

    static async Task<bool> WaitAsync(Func<bool> cond)
    {
        for (var i = 0; i < 100 && !cond(); i++) await Task.Delay(20);
        return cond();
    }

    [Theory]
    [InlineData(false, "poweroff")]
    [InlineData(true, "reboot")]
    public async Task RunsSystemctlAfterTheDelay(bool reboot, string verb)
    {
        var (svc, runner) = Make(false);
        svc.Schedule(reboot);
        Assert.True(await WaitAsync(() => runner.Calls.Any(c => c.File == "systemctl" && c.Args.SequenceEqual([verb]))));
    }

    [Fact]
    public async Task SimulatedModeDoesNothing()
    {
        var (svc, runner) = Make(true);
        svc.Schedule(false);
        await Task.Delay(150);
        Assert.Empty(runner.Calls);
    }
}
