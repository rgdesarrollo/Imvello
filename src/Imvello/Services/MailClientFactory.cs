using System.Collections.Concurrent;
using Imvello.Data;
using Imvello.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace Imvello.Services;

public sealed class MailClientFactory
{
    private readonly MailDatabase _db;
    private readonly OAuth2Service _oauth;
    private readonly ConcurrentDictionary<long, OAuthTokens> _tokens = new();

    public MailClientFactory(MailDatabase db, OAuth2Service oauth)
    {
        _db = db;
        _oauth = oauth;
    }

    public static SecureSocketOptions Map(SecurityMode m) => m switch
    {
        SecurityMode.None => SecureSocketOptions.None,
        SecurityMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SecurityMode.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.Auto
    };

    public void CacheToken(long accountId, OAuthTokens t) => _tokens[accountId] = t;

    public void ClearToken(long accountId) => _tokens.TryRemove(accountId, out _);

    private static bool UsesOAuthProvider(Account a) => a.Kind is AccountKind.Gmail or AccountKind.Outlook;

    /// <summary>Runs connect+authenticate, converting a refused sign-in on Gmail/Outlook into <see cref="ReauthRequiredException"/>.</summary>
    private async Task AuthenticateAsync(Account a, Func<Task> authenticate)
    {
        try { await authenticate(); }
        catch (Exception ex) when (a.Id > 0 && UsesOAuthProvider(a) && ex is AuthenticationException or UnauthorizedAccessException)
        {
            ClearToken(a.Id);
            throw new ReauthRequiredException(a.Id, a.Email, ex);
        }
    }

    private async Task<SaslMechanism?> GetCredentialAsync(Account a, string? secretOverride, CancellationToken ct)
    {
        if (a.AuthType == AuthType.OAuth2)
        {
            if (_tokens.TryGetValue(a.Id, out var t) && t.ExpiresAt > DateTimeOffset.UtcNow)
                return new SaslMechanismOAuth2(a.UserName, t.AccessToken);
            var refresh = secretOverride ?? _db.GetSecret(a.Id);
            if (string.IsNullOrEmpty(refresh)) throw new UnauthorizedAccessException("no refresh token");
            t = await _oauth.RefreshAsync(a.Kind, refresh, ct);
            _tokens[a.Id] = t;
            if (a.Id > 0 && t.RefreshToken != null && t.RefreshToken != refresh) await _db.SetSecretAsync(a.Id, t.RefreshToken);
            return new SaslMechanismOAuth2(a.UserName, t.AccessToken);
        }
        return null;
    }

    public async Task<ImapClient> ConnectImapAsync(Account a, CancellationToken ct, string? secretOverride = null)
    {
        var client = new ImapClient { Timeout = 60000 };
        try
        {
            await client.ConnectAsync(a.ImapHost, a.ImapPort, Map(a.ImapSecurity), ct);
            await AuthenticateAsync(a, async () =>
            {
                var sasl = await GetCredentialAsync(a, secretOverride, ct);
                if (sasl != null) await client.AuthenticateAsync(sasl, ct);
                else await client.AuthenticateAsync(a.UserName, secretOverride ?? _db.GetSecret(a.Id) ?? "", ct);
            });
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<SmtpClient> ConnectSmtpAsync(Account a, CancellationToken ct, string? secretOverride = null)
    {
        var client = new SmtpClient { Timeout = 60000 };
        try
        {
            await client.ConnectAsync(a.SmtpHost, a.SmtpPort, Map(a.SmtpSecurity), ct);
            await AuthenticateAsync(a, async () =>
            {
                var sasl = await GetCredentialAsync(a, secretOverride, ct);
                if (sasl != null) await client.AuthenticateAsync(sasl, ct);
                else if (client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
                    await client.AuthenticateAsync(a.UserName, secretOverride ?? _db.GetSecret(a.Id) ?? "", ct);
            });
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<(bool ok, bool imapOk, bool smtpOk, string message)> TestConnectionAsync(Account a, string? secret, CancellationToken ct)
    {
        try
        {
            using (var imap = await ConnectImapAsync(a, ct, secret))
            {
                await imap.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
                await imap.DisconnectAsync(true, ct);
            }
        }
        catch (Exception ex)
        {
            return (false, false, false, "Incoming mail (IMAP) failed: " + Describe(ex));
        }
        try
        {
            using var smtp = await ConnectSmtpAsync(a, ct, secret);
            await smtp.DisconnectAsync(true, ct);
        }
        catch (Exception ex)
        {
            return (false, true, false, "Outgoing mail (SMTP) failed: " + Describe(ex));
        }
        return (true, true, true, "Congratulations! All tests completed successfully.");
    }

    private static string Describe(Exception ex)
    {
        var msg = ex.Message;
        for (var inner = ex.InnerException; inner != null && !string.IsNullOrWhiteSpace(inner.Message); inner = inner.InnerException)
            msg = inner.Message;
        return msg;
    }
}
