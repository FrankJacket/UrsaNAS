using System.Security.Cryptography;
using System.Text;

namespace Nas.Web.Auth;

/// <summary>
/// Jeton à usage unique pour créer le premier administrateur : sans lui, la première personne qui ouvre l'interface
/// sur le réseau pourrait s'en emparer. Il est écrit dans le journal du service et dans un fichier du dossier de données
/// (lisible seulement par le compte du service et root), puis supprimé quand le compte est créé.
/// </summary>
public sealed class SetupToken
{
    string? _token;
    string? _file;

    public bool Active => _token is not null;

    public string Create(string dataDir)
    {
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        _file = Path.Combine(dataDir, "setup-token.txt");
        File.WriteAllText(_file, _token + Environment.NewLine);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return _token;
    }

    public string? FilePath => _file;

    public bool Verify(string? given) =>
        _token is not null && given is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(_token), Encoding.UTF8.GetBytes(given.Trim()));

    public void Clear()
    {
        _token = null;
        if (_file is not null && File.Exists(_file)) File.Delete(_file);
    }
}
