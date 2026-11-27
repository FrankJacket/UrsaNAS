namespace Nas.Contracts;

/// <summary>The state of a script started by the agent.</summary>
public static class ScriptState
{
    /// <summary><c>Failed</c>: non-zero exit code, or a script that could not be followed (no <c>ExitCode</c>, <c>Error</c> set).</summary>
    public const string Running = "Running", Succeeded = "Succeeded", Failed = "Failed", TimedOut = "TimedOut", Stopped = "Stopped";
}

/// <summary>
/// Request to run a Bash script. <c>Id</c> (32 hexadecimal digits) is chosen by the web interface, which records it before calling the agent.
/// <c>RunAs</c>: “root” or a NAS user.
/// </summary>
public sealed record ScriptRunRequest(string Id, string Script, string RunAs, int TimeoutMinutes);

/// <summary><c>Output</c>: the end of the output (standard and error combined), filled in once the script has finished.</summary>
public sealed record ScriptRunStatus(string Id, string State, int? ExitCode = null, string Output = "", DateTime? EndedUtc = null, string? Error = null, int TimeoutMinutes = 0);

public static class ScriptRules
{
    public const string Root = "root";
    public const int MaxScriptLength = 65536;
    /// <summary>Maximum size, in bytes, of the end of the output that is kept.</summary>
    public const int MaxOutputBytes = 65536;
    public const int DefaultTimeoutMinutes = 60, MaxTimeoutMinutes = 10080;

    /// <summary>Bash does not accept Windows line endings: they are always converted.</summary>
    public static string Normalize(string script) => script.Replace("\r\n", "\n").Replace('\r', '\n');

    public static string? Validate(string? script, string? runAs, int timeoutMinutes)
    {
        if (string.IsNullOrWhiteSpace(script)) return Msg.F("Enter the script.");
        if (script.Length > MaxScriptLength) return Msg.F("The script is too long ({0} characters at most).", MaxScriptLength);
        if (script.Contains('\0')) return Msg.F("The script contains a character that is not allowed.");
        if (string.IsNullOrEmpty(runAs)) return Msg.F("Choose the account that runs the script.");
        if (runAs != Root && Rules.ValidateUserName(runAs) is { } e) return e;
        if (timeoutMinutes is < 1 or > MaxTimeoutMinutes) return Msg.F("The time limit must be between 1 and {0} minutes.", MaxTimeoutMinutes);
        return null;
    }
}
