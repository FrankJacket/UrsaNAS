using System.Diagnostics;

namespace Nas.Agent.Services;

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Lance un programme sans shell : les arguments ne sont jamais concaténés dans une chaîne.
/// <paramref name="stdin"/> sert aux secrets (mots de passe), qui ne doivent jamais apparaître dans les arguments.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default);
}

/// <summary>Un lanceur qui peut aussi rendre compte, ligne par ligne, de ce que le programme écrit pendant qu'il tourne.</summary>
public interface IStreamingCommandRunner : ICommandRunner
{
    Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> args, Action<string> onLine, CancellationToken ct = default);
}

public sealed class ProcessCommandRunner : IStreamingCommandRunner
{
    public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default) =>
        RunCoreAsync(fileName, args, stdin, null, ct);

    public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> args, Action<string> onLine, CancellationToken ct = default) =>
        RunCoreAsync(fileName, args, null, onLine, ct);

    static async Task<string> ReadAsync(StreamReader reader, Action<string>? onLine, CancellationToken ct)
    {
        if (onLine is null) return await reader.ReadToEndAsync(ct);
        var all = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            all.AppendLine(line);
            onLine(line);
        }
        return all.ToString();
    }

    static async Task<CommandResult> RunCoreAsync(string fileName, IReadOnlyList<string> args, string? stdin, Action<string>? onLine, CancellationToken ct)
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
        var stdout = ReadAsync(p.StandardOutput, onLine, ct);
        var stderr = ReadAsync(p.StandardError, onLine, ct);
        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            p.StandardInput.Close();
        }
        await p.WaitForExitAsync(ct);
        return new CommandResult(p.ExitCode, await stdout, await stderr);
    }
}
