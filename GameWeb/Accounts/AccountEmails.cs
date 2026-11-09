using System.Globalization;
using System.Net;
using GameWeb.Data;
using GameWeb.Localization;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Localization;
using MimeKit;

namespace GameWeb.Accounts;

/// <summary>
/// SMTP settings (section "Email"). Put the password on the server like the connection string, as the
/// app-pool variable Email__Password, never in a file in the site folder.
/// </summary>
public sealed class EmailOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    /// <summary>The sender address, e.g. noreply@jadetables.com.</summary>
    public string? From { get; set; }
    public string FromName { get; set; } = "Jade Tables";
    /// <summary>Auto (StartTls on 587, SSL on 465), SslOnConnect, StartTls or None.</summary>
    public string Security { get; set; } = "Auto";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
}

/// <summary>Account emails (confirm your address, reset your password), in the player's language.</summary>
public sealed class AccountEmails(
    Microsoft.Extensions.Options.IOptions<EmailOptions> options,
    IStringLocalizer<SharedResource> l,
    ILogger<AccountEmails> logger)
{
    readonly EmailOptions _options = options.Value;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastSent = new();

    /// <summary>Time between two "send the link again" or "reset password" emails to one account.</summary>
    public static readonly TimeSpan Spacing = TimeSpan.FromMinutes(2);

    public bool CanSend => _options.IsConfigured;

    /// <summary>
    /// True if an email may go to this account now (and notes the time), false if one went out less than
    /// <see cref="Spacing"/> ago. Stops the forms being used to flood someone's inbox.
    /// </summary>
    public bool TryReserve(string userId)
    {
        var now = DateTime.UtcNow;
        if (_lastSent.TryGetValue(userId, out var last) && now - last < Spacing) return false;
        _lastSent[userId] = now;
        if (_lastSent.Count > 10_000)
            foreach (var old in _lastSent.Where(kv => now - kv.Value > Spacing).Select(kv => kv.Key).ToList())
                _lastSent.TryRemove(old, out _);
        return true;
    }

    public Task SendConfirmationAsync(ApplicationUser user, string link) =>
        SendAsync(user, link,
            "Confirm your Jade Tables account",
            "Hello {0}, thanks for joining Jade Tables. Please confirm your email address:",
            "Confirm my email",
            "If you didn't create an account, you can ignore this email.");

    public Task SendPasswordResetAsync(ApplicationUser user, string link) =>
        SendAsync(user, link,
            "Reset your Jade Tables password",
            "Hello {0}, someone (hopefully you) asked to reset the password for your Jade Tables account:",
            "Choose a new password",
            "If you didn't ask for this, you can ignore this email; your password stays the same. The link works for one day.");

    async Task SendAsync(ApplicationUser user, string link, string subjectKey, string introKey, string buttonKey, string footerKey)
    {
        string subject, intro, button, footer;
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            // Write the email in the player's language, not the language of whoever triggered it.
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(string.IsNullOrEmpty(user.Culture) ? "en" : user.Culture);
            subject = l[subjectKey];
            intro = l[introKey, user.DisplayName];
            button = l[buttonKey];
            footer = l[footerKey];
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }

        if (!_options.IsConfigured)
        {
            // No mail server set up (development): log the link instead, so the flow can still be tried.
            logger.LogWarning("Email not configured. Would send \"{Subject}\" to {Email}: {Link}", subject, user.Email, link);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.From!));
        message.To.Add(new MailboxAddress(user.DisplayName, user.Email!));
        message.Subject = subject;
        string safeLink = WebUtility.HtmlEncode(link);
        message.Body = new BodyBuilder
        {
            TextBody = $"{intro}\n\n{link}\n\n{footer}\n\nJade Tables · www.jadetables.com",
            HtmlBody = $"""
                <div style="font-family:Segoe UI,Arial,sans-serif;font-size:15px;line-height:1.5;color:#1d2422;max-width:520px">
                  <p>{WebUtility.HtmlEncode(intro)}</p>
                  <p><a href="{safeLink}" style="display:inline-block;background:#c23b22;color:#fff;padding:10px 18px;border-radius:999px;text-decoration:none;font-weight:600">{WebUtility.HtmlEncode(button)}</a></p>
                  <p style="font-size:13px;color:#6f7a75">{WebUtility.HtmlEncode(footer)}</p>
                  <p style="font-size:12px;color:#6f7a75;word-break:break-all">{safeLink}</p>
                  <p style="font-size:12px;color:#6f7a75">Jade Tables · 玉桌 · www.jadetables.com</p>
                </div>
                """,
        }.ToMessageBody();

        using var client = new SmtpClient();
        var security = _options.Security switch
        {
            "SslOnConnect" => SecureSocketOptions.SslOnConnect,
            "StartTls" => SecureSocketOptions.StartTls,
            "None" => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto,
        };
        await client.ConnectAsync(_options.Host!, _options.Port, security);
        if (!string.IsNullOrEmpty(_options.UserName))
            await client.AuthenticateAsync(_options.UserName, _options.Password ?? "");
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }
}
