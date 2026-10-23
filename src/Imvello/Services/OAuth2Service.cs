using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Imvello.Models;

namespace Imvello.Services;

public record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? Email);

/// <summary>The stored sign-in for an OAuth account is missing, expired or revoked; the user must sign in again.</summary>
public sealed class ReauthRequiredException(long accountId, string email, Exception? inner = null)
    : Exception(Loc.F("Main.ReauthRequired", email), inner)
{
    public long AccountId { get; } = accountId;
}

public sealed class OAuth2Service
{
    private static readonly HttpClient Http = new();
    private readonly AppSettings _settings;

    public OAuth2Service(AppSettings settings) => _settings = settings;

    private (string auth, string token, string scope, string clientId, string? secret) Provider(AccountKind kind)
    {
        if (kind == AccountKind.Gmail)
        {
            if (!TryParseGoogleClient(_settings.GoogleClientId, _settings.GoogleClientSecret, out var id, out var secret))
                throw new InvalidOperationException(Loc.T("Dialogs.GoogleClientIdInvalid"));
            if (string.IsNullOrEmpty(secret))
                throw new InvalidOperationException(Loc.T("Dialogs.GoogleClientSecretMissing"));
            return ("https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token",
                "https://mail.google.com/ openid email", id, secret);
        }
        if (kind == AccountKind.Outlook)
        {
            if (!TryParseMicrosoftClient(_settings.MicrosoftClientId, out var id))
                throw new InvalidOperationException(Loc.T("Dialogs.MicrosoftClientIdInvalid"));
            return ("https://login.microsoftonline.com/common/oauth2/v2.0/authorize", "https://login.microsoftonline.com/common/oauth2/v2.0/token",
                "https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send offline_access openid email",
                id, null);
        }
        throw new InvalidOperationException("OAuth2 is only supported for Gmail and Outlook accounts.");
    }

    private static readonly System.Text.RegularExpressions.Regex GoogleClientIdPattern =
        new(@"\d+-[a-z0-9]+\.apps\.googleusercontent\.com", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Extracts a Google OAuth client ID (and secret) from what the user pasted: the bare ID, the ID with stray
    /// quotes/spaces, or the whole client_secret JSON downloaded from Google Cloud Console.
    /// </summary>
    public static bool TryParseGoogleClient(string? idText, string? secretText, out string clientId, out string? clientSecret)
    {
        clientId = "";
        clientSecret = string.IsNullOrWhiteSpace(secretText) ? null : secretText.Trim().Trim('"', '\'').Trim();
        if (string.IsNullOrWhiteSpace(idText)) return false;
        var text = idText.Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                foreach (var section in new[] { "installed", "web" })
                    if (root.TryGetProperty(section, out var s)) { root = s; break; }
                if (root.TryGetProperty("client_secret", out var sec) && sec.GetString() is { Length: > 0 } secVal) clientSecret = secVal;
                if (root.TryGetProperty("client_id", out var cid)) text = cid.GetString() ?? "";
            }
            catch (JsonException) { }
        }
        var m = GoogleClientIdPattern.Match(text);
        if (!m.Success) return false;
        clientId = m.Value.ToLowerInvariant();
        return true;
    }

    private static readonly System.Text.RegularExpressions.Regex GuidPattern =
        new(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Extracts the Azure "Application (client) ID" GUID from what the user pasted, ignoring stray quotes, spaces or labels.
    /// </summary>
    public static bool TryParseMicrosoftClient(string? text, out string clientId)
    {
        clientId = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = GuidPattern.Match(text);
        if (!m.Success) return false;
        clientId = m.Value.ToLowerInvariant();
        return true;
    }

    public static bool IsMicrosoftConfigured(AppSettings s) => TryParseMicrosoftClient(s.MicrosoftClientId, out _);

    public static bool IsGoogleConfigured(AppSettings s) =>
        TryParseGoogleClient(s.GoogleClientId, s.GoogleClientSecret, out _, out var secret) && !string.IsNullOrEmpty(secret);

    public async Task<OAuthTokens> AuthorizeAsync(AccountKind kind, string? loginHint, CancellationToken ct)
    {
        var p = Provider(kind);
        var port = GetFreePort();
        var redirect = kind == AccountKind.Outlook ? $"http://localhost:{port}/" : $"http://127.0.0.1:{port}/";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var url = $"{p.auth}?response_type=code&client_id={Uri.EscapeDataString(p.clientId)}&redirect_uri={Uri.EscapeDataString(redirect)}" +
                  $"&scope={Uri.EscapeDataString(p.scope)}&code_challenge={challenge}&code_challenge_method=S256&state={state}" +
                  (kind == AccountKind.Gmail ? "&access_type=offline&prompt=consent" : "&prompt=select_account") +
                  (string.IsNullOrWhiteSpace(loginHint) ? "" : "&login_hint=" + Uri.EscapeDataString(loginHint));

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });
        var ctx = await listener.GetContextAsync();
        var q = ctx.Request.QueryString;
        var html = "<html><body style='font-family:Segoe UI;padding:40px'><h2>Imvello</h2><p>You can close this window and return to Imvello.</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        ctx.Response.Close();

        if (q["error"] is { } err)
            throw new InvalidOperationException("Sign-in failed: " + err + " " + q["error_description"] + ErrorHint(kind, err));
        if (q["state"] != state || q["code"] is not { } code) throw new InvalidOperationException("Sign-in failed: invalid response.");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect,
            ["client_id"] = p.clientId, ["code_verifier"] = verifier
        };
        if (!string.IsNullOrEmpty(p.secret)) form["client_secret"] = p.secret!;
        return await TokenRequestAsync(p.token, form, ct);
    }

    public async Task<OAuthTokens> RefreshAsync(AccountKind kind, string refreshToken, CancellationToken ct)
    {
        var p = Provider(kind);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken, ["client_id"] = p.clientId
        };
        if (kind == AccountKind.Outlook) form["scope"] = p.scope;
        if (!string.IsNullOrEmpty(p.secret)) form["client_secret"] = p.secret!;
        var t = await TokenRequestAsync(p.token, form, ct);
        return t with { RefreshToken = t.RefreshToken ?? refreshToken };
    }

    private static string ErrorHint(AccountKind kind, string err) => kind == AccountKind.Outlook
        ? err is "invalid_client" or "unauthorized_client" or "invalid_request" or "redirect_uri_mismatch" ? "\n\n" + Loc.T("Dialogs.MicrosoftOAuthErrorClient") : ""
        : err switch
    {
        "redirect_uri_mismatch" => "\n\n" + Loc.T("Dialogs.OAuthErrorMismatch"),
        "access_denied" => "\n\n" + Loc.T("Dialogs.OAuthErrorBlocked"),
        "invalid_client" or "unauthorized_client" or "invalid_grant" => "\n\n" + Loc.T("Dialogs.OAuthErrorInvalidClient"),
        _ => ""
    };

    private static async Task<OAuthTokens> TokenRequestAsync(string endpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await Http.PostAsync(endpoint, new FormUrlEncodedContent(form), ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            if (json.Contains("invalid_grant", StringComparison.Ordinal)) throw new UnauthorizedAccessException("invalid_grant");
            throw new InvalidOperationException("Token request failed: " + json);
        }
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var access = r.GetProperty("access_token").GetString()!;
        var refresh = r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var expires = r.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
        string? email = null;
        if (r.TryGetProperty("id_token", out var idt) && idt.GetString() is { } jwt) email = EmailFromJwt(jwt);
        return new OAuthTokens(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expires - 60), email);
    }

    private static string? EmailFromJwt(string jwt)
    {
        try
        {
            var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            part = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(part));
            foreach (var key in new[] { "email", "preferred_username", "upn" })
                if (doc.RootElement.TryGetProperty(key, out var v)) return v.GetString();
        }
        catch { }
        return null;
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
