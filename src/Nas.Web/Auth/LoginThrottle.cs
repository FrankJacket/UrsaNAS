using System.Collections.Concurrent;

namespace Nas.Web.Auth;

/// <summary>
/// Freine les essais de mots de passe : 5 échecs sur un même compte, ou 20 depuis une même adresse, bloquent la connexion
/// pendant 5 minutes. Les compteurs sont en mémoire (ils repartent à zéro au redémarrage) et oubliés après 10 minutes d'accalmie.
/// </summary>
public sealed class LoginThrottle(TimeProvider? clock = null)
{
    public const int MaxPerUser = 5, MaxPerIp = 20;
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5), Forget = TimeSpan.FromMinutes(10);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Last)> _fails = new();

    static string UserKey(string name) => "u:" + name.Trim().ToLowerInvariant();
    static string IpKey(string ip) => "i:" + ip;

    public bool IsLocked(string name, string ip) => Locked(UserKey(name), MaxPerUser) || Locked(IpKey(ip), MaxPerIp);

    bool Locked(string key, int max) =>
        _fails.TryGetValue(key, out var f) && f.Count >= max && _clock.GetUtcNow() - f.Last < Lockout;

    public void Fail(string name, string ip)
    {
        Bump(UserKey(name));
        Bump(IpKey(ip));
        var now = _clock.GetUtcNow();
        foreach (var (k, v) in _fails)
            if (now - v.Last > Forget) _fails.TryRemove(k, out _);
    }

    /// <summary>Une connexion réussie remet à zéro le compteur du compte (pas celui de l'adresse).</summary>
    public void Success(string name) => _fails.TryRemove(UserKey(name), out _);

    void Bump(string key)
    {
        var now = _clock.GetUtcNow();
        _fails.AddOrUpdate(key, (1, now), (_, old) => now - old.Last > Forget ? (1, now) : (old.Count + 1, now));
    }
}
