using Imvello.Data;
using Imvello.Models;

namespace Imvello.Services;

public sealed class AccountService
{
    public const string LocalFoldersName = "Local Folders";

    private readonly MailDatabase _db;
    private readonly MessageStore _store;
    private readonly SyncService _sync;
    private readonly MailClientFactory _clients;
    private readonly OAuth2Service _oauth;

    public event EventHandler? AccountsChanged;

    public AccountService(MailDatabase db, MessageStore store, SyncService sync, MailClientFactory clients, OAuth2Service oauth)
    {
        _db = db;
        _store = store;
        _sync = sync;
        _clients = clients;
        _oauth = oauth;
    }

    public List<Account> GetAccounts() => _db.GetAccounts();

    public Account LocalAccount => _db.GetAccounts().First(a => a.Kind == AccountKind.Local);

    public async Task EnsureLocalAccountAsync()
    {
        var local = _db.GetAccounts().FirstOrDefault(a => a.Kind == AccountKind.Local);
        if (local == null)
        {
            local = new Account { DisplayName = LocalFoldersName, Email = LocalFoldersName, Kind = AccountKind.Local, SortOrder = 10000 };
            await _db.InsertAccountAsync(local);
        }
        if (_db.GetFolders(local.Id).Count == 0)
        {
            foreach (var (name, special) in new[] { ("Inbox", SpecialFolder.Inbox), ("Imported", SpecialFolder.None), ("Archive", SpecialFolder.Archive), ("Trash", SpecialFolder.Trash) })
                await _db.UpsertFolderAsync(new MailFolder { AccountId = local.Id, FullName = name, DisplayName = name, Special = special, Delimiter = "/" });
        }
    }

    public Task<OAuthTokens> SignInAsync(AccountKind kind, string? email, CancellationToken ct) =>
        _oauth.AuthorizeAsync(kind, email, ct);

    public Task<(bool ok, bool imapOk, bool smtpOk, string message)> TestAsync(Account a, string secret, CancellationToken ct) =>
        _clients.TestConnectionAsync(a, secret, ct);

    public async Task<Account> AddAccountAsync(Account a, string secret, OAuthTokens? tokens, CancellationToken ct = default)
    {
        MailProviderDefaults.Apply(a);
        a.SortOrder = _db.GetAccounts().Count(x => x.Kind != AccountKind.Local);
        await _db.InsertAccountAsync(a);
        await _db.SetSecretAsync(a.Id, tokens?.RefreshToken ?? secret);
        if (tokens != null) _clients.CacheToken(a.Id, tokens);
        AccountsChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _sync.SyncFolderListAsync(a.Id, ct);
        }
        catch (Exception ex)
        {
            _sync.ReportStatus($"{a.Email}: {ex.Message}", null, a.Id);
        }
        _ = Task.Run(() => _sync.SyncAccountAsync(a.Id, CancellationToken.None));
        return a;
    }

    public async Task UpdateAccountAsync(Account a, string? newSecret)
    {
        await _db.UpdateAccountAsync(a);
        if (!string.IsNullOrEmpty(newSecret)) await _db.SetSecretAsync(a.Id, newSecret);
        _sync.DropSession(a.Id);
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    public static bool CanReconnect(Account a) => a.Kind is AccountKind.Gmail or AccountKind.Outlook;

    /// <summary>
    /// Opens the browser to sign in to Google/Microsoft again and stores the new sign-in for an existing account.
    /// Also upgrades an App Password account to browser sign-in. Mail already downloaded is kept.
    /// </summary>
    public async Task ReconnectAsync(long accountId, CancellationToken ct)
    {
        var a = _db.GetAccount(accountId) ?? throw new InvalidOperationException("Account not found.");
        if (!CanReconnect(a)) throw new InvalidOperationException("OAuth2 is only supported for Gmail and Outlook accounts.");

        var tokens = await _oauth.AuthorizeAsync(a.Kind, a.Email, ct);
        if (!string.IsNullOrWhiteSpace(tokens.Email) && !string.Equals(tokens.Email, a.Email, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.F("Main.ReconnectWrongAccount", tokens.Email, a.Email));
        if (string.IsNullOrEmpty(tokens.RefreshToken))
            throw new InvalidOperationException(Loc.T("Main.ReconnectNoRefreshToken"));

        _sync.DropSession(a.Id);
        a.AuthType = AuthType.OAuth2;
        a.UserName = a.Email;
        await _db.UpdateAccountAsync(a);
        await _db.SetSecretAsync(a.Id, tokens.RefreshToken);
        _clients.CacheToken(a.Id, tokens);

        // Prove the new sign-in works against the mail server before reporting success.
        await _sync.SyncFolderListAsync(a.Id, ct);
        AccountsChanged?.Invoke(this, EventArgs.Empty);
        _ = Task.Run(() => _sync.SyncAccountAsync(a.Id, CancellationToken.None));
    }

    public async Task RemoveAccountAsync(long accountId)
    {
        _sync.DropSession(accountId);
        await _db.DeleteAccountAsync(accountId);
        _store.DeleteAccount(accountId);
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }
}
