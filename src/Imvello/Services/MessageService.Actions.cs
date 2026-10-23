using Imvello.Models;
using MailKit;
using MailKit.Net.Imap;
using MimeKit;
using LmFlags = Imvello.Models.MessageFlags;
using SpecialFolder = Imvello.Models.SpecialFolder;
using MailFolder = Imvello.Models.MailFolder;

namespace Imvello.Services;

public sealed partial class MessageService
{
    private async Task<List<IGrouping<long, MessageHeader>>> GroupByFolderAsync(IEnumerable<long> ids) =>
        (await _db.GetMessagesAsync(ids)).GroupBy(h => h.FolderId).ToList();

    private bool IsLocal(long accountId) => _db.GetAccount(accountId)?.Kind == AccountKind.Local;

    private async Task RefreshCountsAsync(long folderId, long accountId)
    {
        await _db.RecalcFolderCountsAsync(folderId);
        _sync.RaiseFolderChanged(folderId, accountId);
    }

    public Task SetReadAsync(IEnumerable<long> ids, bool read, CancellationToken ct = default) =>
        SetBitsAsync(ids, LmFlags.Seen, read, ct);

    public Task SetFlaggedAsync(IEnumerable<long> ids, bool flagged, CancellationToken ct = default) =>
        SetBitsAsync(ids, LmFlags.Flagged, flagged, ct);

    public async Task SetBitsAsync(IEnumerable<long> ids, LmFlags bits, bool on, CancellationToken ct = default)
    {
        foreach (var g in await GroupByFolderAsync(ids))
        {
            var headers = g.ToList();
            var accountId = headers[0].AccountId;
            await _db.SetFlagBitsAsync(headers.Select(h => h.Id), bits, on);
            await RefreshCountsAsync(g.Key, accountId);
            if (IsLocal(accountId)) continue;
            try
            {
                var folder = _db.GetFolder(g.Key)!;
                await _sync.WithClientAsync(accountId, async client =>
                {
                    var f = await client.GetFolderAsync(folder.FullName, ct);
                    await f.OpenAsync(FolderAccess.ReadWrite, ct);
                    var uids = headers.Select(h => new UniqueId((uint)h.Uid)).ToList();
                    if (bits.HasFlag(LmFlags.Forwarded))
                    {
                        var kw = new HashSet<string> { "$Forwarded" };
                        if (on) await f.AddFlagsAsync(uids, MailKit.MessageFlags.None, kw, true, ct);
                        else await f.RemoveFlagsAsync(uids, MailKit.MessageFlags.None, kw, true, ct);
                    }
                    var mk = bits.ToMailKit();
                    if (mk != MailKit.MessageFlags.None)
                    {
                        if (on) await f.AddFlagsAsync(uids, mk, true, ct);
                        else await f.RemoveFlagsAsync(uids, mk, true, ct);
                    }
                }, ct);
            }
            catch
            {
                foreach (var h in headers) await _db.UpdateFlagsAsync([h.Id], h.Flags);
                await RefreshCountsAsync(g.Key, accountId);
                throw;
            }
        }
    }

    public async Task DeleteAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        foreach (var g in await GroupByFolderAsync(ids))
        {
            var folder = _db.GetFolder(g.Key)!;
            var trash = _db.GetSpecialFolder(folder.AccountId, SpecialFolder.Trash);
            if (trash == null || trash.Id == folder.Id) await PermanentDeleteAsync(folder, g.ToList(), ct);
            else await MoveAsync(g.Select(h => h.Id), trash.Id, ct);
        }
    }

    public async Task PermanentDeleteAsync(MailFolder folder, List<MessageHeader> headers, CancellationToken ct = default)
    {
        if (!IsLocal(folder.AccountId))
        {
            await _sync.WithClientAsync(folder.AccountId, async client =>
            {
                var f = await client.GetFolderAsync(folder.FullName, ct);
                await f.OpenAsync(FolderAccess.ReadWrite, ct);
                var uids = headers.Select(h => new UniqueId((uint)h.Uid)).ToList();
                await f.AddFlagsAsync(uids, MailKit.MessageFlags.Deleted, true, ct);
                if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus)) await f.ExpungeAsync(uids, ct);
                else await f.ExpungeAsync(ct);
            }, ct);
        }
        await _db.DeleteMessagesAsync(headers.Select(h => h.Id));
        foreach (var h in headers) _store.Delete(h.AccountId, h.Id);
        await RefreshCountsAsync(folder.Id, folder.AccountId);
    }

    public async Task ArchiveAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        foreach (var g in await GroupByFolderAsync(ids))
        {
            var folder = _db.GetFolder(g.Key)!;
            var account = _db.GetAccount(folder.AccountId)!;
            var target = account.Kind == AccountKind.Gmail
                ? _db.GetSpecialFolder(account.Id, SpecialFolder.All) ?? _db.GetSpecialFolder(account.Id, SpecialFolder.Archive)
                : _db.GetSpecialFolder(account.Id, SpecialFolder.Archive);
            if (target == null)
            {
                await _sync.CreateFolderAsync(account.Id, null, "Archive", ct);
                target = _db.GetFolders(account.Id).FirstOrDefault(f => f.DisplayName == "Archive")
                         ?? throw new InvalidOperationException("Could not create Archive folder.");
            }
            if (target.Id == folder.Id) continue;
            await MoveAsync(g.Select(h => h.Id), target.Id, ct);
        }
    }

    public async Task MarkJunkAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        foreach (var g in await GroupByFolderAsync(ids))
        {
            var folder = _db.GetFolder(g.Key)!;
            var junk = _db.GetSpecialFolder(folder.AccountId, SpecialFolder.Junk)
                       ?? throw new InvalidOperationException("This account has no Junk Email folder.");
            if (junk.Id != folder.Id) await MoveAsync(g.Select(h => h.Id), junk.Id, ct);
        }
    }
}
