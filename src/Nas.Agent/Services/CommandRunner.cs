using System.Diagnostics;

namespace Nas.Agent.Services;

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Lance un programme sans shell : les arguments ne sont jamais concaténés dans une chaîne.
/// <paramref name="stdin"/> sert aux secrets (mots de passe), qui ne doivent jamais apparaître dans les arguments.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default);
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException(Nas.Contracts.Msg.F("Unable to start {0}.", fileName));
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            p.StandardInput.Close();
        }
        await p.WaitForExitAsync(ct);
        return new CommandResult(p.ExitCode, await stdout, await stderr);
    }
}
