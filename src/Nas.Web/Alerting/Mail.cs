using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Nas.Web.Alerting;

/// <summary><c>Security</c> : <c>none</c>, <c>starttls</c> (port 587) ou <c>ssl</c> (port 465).</summary>
public sealed record MailSettings(bool Enabled, string Host, int Port, string Security, string User, string Password, string From, string To)
{
    public static MailSettings Default { get; } = new(false, "", 587, "starttls", "", "", "", "");
    public IReadOnlyList<string> Recipients => To.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public static partial class MailRules
{
    public static readonly IReadOnlyList<string> SecurityModes = ["none", "starttls", "ssl"];

    [GeneratedRegex(@"^[^@\s<>,;]+@[^@\s<>,;]+\.[^@\s<>,;]+$")]
    private static partial Regex EmailRegex();

    public static bool IsEmail(string? s) => !string.IsNullOrEmpty(s) && s.Length <= 254 && !s.Any(char.IsControl) && EmailRegex().IsMatch(s);

    /// <summary>Valide les réglages avant de les enregistrer. Désactivés, ils ne sont pas vérifiés : on peut les enregistrer à moitié remplis.</summary>
    public static string? Validate(MailSettings s)
    {
        if (!s.Enabled) return null;
        if (string.IsNullOrWhiteSpace(s.Host) || s.Host.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return "Serveur SMTP invalide.";
        if (s.Port is < 1 or > 65535) return "Port SMTP invalide (1 à 65535).";
        if (!SecurityModes.Contains(s.Security)) return "Sécurité de connexion inconnue.";
        if (!IsEmail(s.From)) return "Adresse de l'expéditeur invalide.";
        var to = s.Recipients;
        if (to.Count is 0 or > 5) return "Indique de 1 à 5 destinataires.";
        if (to.FirstOrDefault(a => !IsEmail(a)) is { } bad) return $"Adresse invalide : {bad}.";
        if (s.User.Any(char.IsControl) || s.Password.Any(char.IsControl)) return "Identifiant ou mot de passe invalide.";
        return null;
    }
}

public interface IMailSender
{
    Task SendAsync(MailSettings settings, string subject, string body, CancellationToken ct = default);
}

public sealed class MailKitSender : IMailSender
{
    public async Task SendAsync(MailSettings s, string subject, string body, CancellationToken ct = default)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(s.From));
        foreach (var to in s.Recipients) message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient { Timeout = 20_000 };
        await client.ConnectAsync(s.Host, s.Port, s.Security switch
        {
            "ssl" => SecureSocketOptions.SslOnConnect,
            "starttls" => SecureSocketOptions.StartTls,
            _ => SecureSocketOptions.None,
        }, ct);
        if (!string.IsNullOrEmpty(s.User)) await client.AuthenticateAsync(s.User, s.Password, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}
