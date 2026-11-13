namespace Nas.Contracts;

/// <summary>Demande de sauvegarde des réglages du NAS en ZIP ; <c>Password</c> vide ou absent = pas de protection.</summary>
public sealed record ExportConfigRequest(string? Password);

public static partial class Rules
{
    public static bool IsZipPassword(string? p) => p is null or { Length: <= 128 };
}
