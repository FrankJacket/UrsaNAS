using System.Text;
using Nas.Agent.Services;

namespace Nas.Agent.Tests;

public class TerminalTests
{
    [Theory]
    [InlineData("{\"type\":\"resize\",\"cols\":120,\"rows\":40}", true, 120, 40)]
    [InlineData("{\"type\":\"resize\",\"cols\":0,\"rows\":40}", false, 0, 0)]
    [InlineData("{\"type\":\"other\",\"cols\":80,\"rows\":24}", false, 0, 0)]
    [InlineData("{\"cols\":80,\"rows\":24}", false, 0, 0)]
    [InlineData("not json", false, 0, 0)]
    [InlineData("[1,2]", false, 0, 0)]
    public void ResizeMessage(string json, bool ok, int cols, int rows)
    {
        Assert.Equal(ok, TerminalEndpoint.TryParseResize(Encoding.UTF8.GetBytes(json), out var c, out var r));
        if (ok) { Assert.Equal(cols, c); Assert.Equal(rows, r); }
    }

    [Fact]
    public async Task ShellRunsOnATerminal()
    {
        if (!OperatingSystem.IsLinux()) return; // le pseudo-terminal n'existe que sous Linux
        using var pty = PtySession.Start("/bin/sh", ["-c", "echo ready; stty size; read x; echo got-$x"], 100, 30);
        var text = new StringBuilder();
        var buf = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sent = false;
        while (!cts.IsCancellationRequested && !text.ToString().Contains("got-abc"))
        {
            int n;
            try { n = await pty.Output.ReadAsync(buf, cts.Token); } catch (IOException) { break; }
            if (n == 0) break;
            text.Append(Encoding.UTF8.GetString(buf, 0, n));
            if (!sent && text.ToString().Contains("30 100")) { await pty.Input.WriteAsync("abc\n"u8.ToArray(), cts.Token); sent = true; }
        }
        Assert.Contains("ready", text.ToString());
        Assert.Contains("30 100", text.ToString()); // la taille demandée est celle du terminal
        Assert.Contains("got-abc", text.ToString());
    }
}
