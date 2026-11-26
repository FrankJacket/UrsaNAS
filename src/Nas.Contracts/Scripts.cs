namespace Nas.Contracts;

/// <summary>Où en est un script lancé par l'agent.</summary>
public static class ScriptState
{
    /// <summary><c>Failed</c> : code de sortie différent de zéro, ou script impossible à suivre (<c>ExitCode</c> absent, <c>Error</c> rempli).</summary>
    public const string Running = "Running", Succeeded = "Succeeded", Failed = "Failed", TimedOut = "TimedOut", Stopped = "Stopped";
}

/// <summary>
/// Demande d'exécution d'un script Bash. <c>Id</c> (32 chiffres hexadécimaux) est choisi par l'interface, qui le note avant d'appeler l'agent.
/// <c>RunAs</c> : « root » ou un utilisateur du NAS.
/// </summary>
public sealed record ScriptRunRequest(string Id, string Script, string RunAs, int TimeoutMinutes);

/// <summary><c>Output</c> : la fin de la sortie (standard et d'erreur mêlées), remplie une fois le script terminé.</summary>
public sealed record ScriptRunStatus(string Id, string State, int? ExitCode = null, string Output = "", DateTime? EndedUtc = null, string? Error = null, int TimeoutMinutes = 0);

public static class ScriptRules
{
    public const string Root = "root";
    public const int MaxScriptLength = 65536;
    /// <summary>Taille maximale, en octets, de la fin de sortie conservée.</summary>
    public const int MaxOutputBytes = 65536;
    public const int DefaultTimeoutMinutes = 60, MaxTimeoutMinutes = 10080;

    /// <summary>Bash n'accepte pas les fins de ligne Windows : elles sont toujours converties.</summary>
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
