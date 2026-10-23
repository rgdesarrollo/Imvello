using System.Collections.Concurrent;
using System.IO;
using Imvello.Data;
using Imvello.Models;
using MailKit;
using SpecialFolder = Imvello.Models.SpecialFolder;
using MailKit.Net.Imap;

namespace Imvello.Services;

public class SyncStatusEventArgs(string text, double? progress, long? accountId) : EventArgs
{
    public string Text { get; } = text;
    public double? Progress { get; } = progress;
    public long? AccountId { get; } = accountId;
}

public class FolderChangedEventArgs(long folderId, long accountId) : EventArgs
{
    public long FolderId { get; } = folderId;
    public long AccountId { get; } = accountId;
}

public class AccountEventArgs(long accountId) : EventArgs
{
    public long AccountId { get; } = accountId;
}

public sealed partial class SyncService
{
    private sealed class Session
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public ImapClient? Client;
    }

    private readonly MailDatabase _db;
    private readonly MessageStore _store;
    private readonly MailClientFactory _clients;
    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<long, Session> _sessions = new();
    private readonly ConcurrentDictionary<long, Session> _fetchSessions = new();
    private readonly SemaphoreSlim _syncAllLock = new(1, 1);
    private Timer? _timer;

    public event EventHandler<SyncStatusEventArgs>? StatusChanged;
    public event EventHandler<FolderChangedEventArgs>? FolderChanged;
    public event EventHandler<AccountEventArgs>? FoldersListChanged;
    /// <summary>Raised when an account's Google/Microsoft sign-in has expired or been revoked.</summary>
    public event EventHandler<AccountEventArgs>? ReauthRequired;

    public bool IsSyncing { get; private set; }
    public bool WorkOffline { get; set; }

    public SyncService(MailDatabase db, MessageStore store, MailClientFactory clients, AppSettings settings)
    {
        _db = db;
        _store = store;
        _clients = clients;
        _settings = settings;
    }

    public void ReportStatus(string text, double? progress = null, long? accountId = null) =>
        StatusChanged?.Invoke(this, new SyncStatusEventArgs(text, progress, accountId));

    public void RaiseFolderChanged(long folderId, long accountId) =>
        FolderChanged?.Invoke(this, new FolderChangedEventArgs(folderId, accountId));

    public void RaiseFoldersListChanged(long accountId) =>
        FoldersListChanged?.Invoke(this, new AccountEventArgs(accountId));

    public void Start()
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _settings.SyncIntervalMinutes));
        _timer?.Dispose();
        _timer = new Timer(_ => _ = SyncAllAsync(CancellationToken.None), null, TimeSpan.FromSeconds(2), interval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public async Task<T> WithClientAsync<T>(long accountId, Func<ImapClient, Task<T>> action, CancellationToken ct, bool interactive = false)
    {
        if (WorkOffline) throw new InvalidOperationException("Imvello is working offline.");
        var account = _db.GetAccount(accountId) ?? throw new InvalidOperationException("Account not found.");
        if (account.Kind == AccountKind.Local) throw new InvalidOperationException("Local account has no server.");
        var s = (interactive ? _fetchSessions : _sessions).GetOrAdd(accountId, _ => new Session());
        await s.Lock.WaitAsync(ct);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (s.Client == null || !s.Client.IsConnected || !s.Client.IsAuthenticated)
                    {
                        s.Client?.Dispose();
                        s.Client = null;
                        s.Client = await _clients.ConnectImapAsync(account, ct);
                    }
                    return await action(s.Client);
                }
                catch (Exception ex) when (attempt == 0 && (ex is ServiceNotConnectedException || ex is IOException || ex is ImapProtocolException))
                {
                    s.Client?.Dispose();
                    s.Client = null;
                }
            }
        }
        finally
        {
            s.Lock.Release();
        }
    }

    public Task WithClientAsync(long accountId, Func<ImapClient, Task> action, CancellationToken ct) =>
        WithClientAsync<bool>(accountId, async c => { await action(c); return true; }, ct);

    public void DropSession(long accountId)
    {
        if (_sessions.TryRemove(accountId, out var s))
        {
            try { s.Client?.Dispose(); } catch { }
        }
        if (_fetchSessions.TryRemove(accountId, out var fs))
        {
            try { fs.Client?.Dispose(); } catch { }
        }
    }

    public async Task SyncAllAsync(CancellationToken ct)
    {
        if (WorkOffline) { ReportStatus("Working Offline"); return; }
        if (!await _syncAllLock.WaitAsync(0, ct)) return;
        IsSyncing = true;
        try
        {
            foreach (var a in _db.GetAccounts().Where(a => a.Enabled && a.Kind != AccountKind.Local))
            {
                ct.ThrowIfCancellationRequested();
                await SyncAccountAsync(a.Id, ct);
            }
            ReportStatus(Loc.T("Main.AllFoldersUpToDate"));
        }
        catch (OperationCanceledException)
        {
            ReportStatus("Send/Receive canceled.");
        }
        finally
        {
            IsSyncing = false;
            _syncAllLock.Release();
        }
    }

    public async Task SyncAccountAsync(long accountId, CancellationToken ct)
    {
        var account = _db.GetAccount(accountId);
        if (account == null || account.Kind == AccountKind.Local) return;
        try
        {
            ReportStatus($"Connecting to {account.Email}...", null, accountId);
            await SyncFolderListAsync(accountId, ct);
            var folders = _db.GetFolders(accountId).Where(f => f.Selectable && f.Special != SpecialFolder.All)
                .OrderBy(f => f.Special == SpecialFolder.Inbox ? 0 : f.Special != SpecialFolder.None ? 1 : 2).ToList();
            foreach (var f in folders)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await SyncFolderAsync(f.Id, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ReportStatus($"Error syncing {f.DisplayName} ({account.Email}): {ex.Message}", null, accountId);
                }
            }
            if (_settings.DownloadFullMessages) await DownloadBodiesAsync(accountId, folders, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (ReauthRequiredException ex)
        {
            ReportStatus(ex.Message, null, accountId);
            ReauthRequired?.Invoke(this, new AccountEventArgs(accountId));
        }
        catch (Exception ex)
        {
            ReportStatus($"{account.Email}: {ex.Message}", null, accountId);
        }
    }
}
