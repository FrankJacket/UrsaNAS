using System.Diagnostics;

namespace Nas.Agent.Services;

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Lance un programme sans shell : les arguments ne sont jamais concaténés dans une chaîne.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct = default);
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Impossible de lancer {fileName}.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return new CommandResult(p.ExitCode, await stdout, await stderr);
    }
}
