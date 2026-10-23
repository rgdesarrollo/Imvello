using Imvello.Models;
using MailKit;
using SpecialFolder = Imvello.Models.SpecialFolder;
using MailFolder = Imvello.Models.MailFolder;
using MailKit.Net.Imap;

namespace Imvello.Services;

public sealed partial class SyncService
{
    private static SpecialFolder DetectSpecial(IMailFolder f, ImapClient client)
    {
        var a = f.Attributes;
        if ((a & FolderAttributes.Inbox) != 0 || f == client.Inbox) return SpecialFolder.Inbox;
        if ((a & FolderAttributes.Sent) != 0) return SpecialFolder.Sent;
        if ((a & FolderAttributes.Drafts) != 0) return SpecialFolder.Drafts;
        if ((a & FolderAttributes.Trash) != 0) return SpecialFolder.Trash;
        if ((a & FolderAttributes.Junk) != 0) return SpecialFolder.Junk;
        if ((a & FolderAttributes.Archive) != 0) return SpecialFolder.Archive;
        if ((a & FolderAttributes.All) != 0) return SpecialFolder.All;
        return f.Name.ToLowerInvariant() switch
        {
            "sent" or "sent items" or "sent mail" or "sent messages" or "enviados" => SpecialFolder.Sent,
            "drafts" or "draft" or "borradores" => SpecialFolder.Drafts,
            "trash" or "deleted" or "deleted items" or "deleted messages" or "bin" or "papelera" => SpecialFolder.Trash,
            "junk" or "spam" or "junk email" or "junk e-mail" or "bulk mail" => SpecialFolder.Junk,
            "archive" or "archives" => SpecialFolder.Archive,
            "outbox" => SpecialFolder.Outbox,
            _ => SpecialFolder.None
        };
    }

    public async Task SyncFolderListAsync(long accountId, CancellationToken ct)
    {
        var remote = await WithClientAsync(accountId, async client =>
        {
            var ns = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', "");
            var all = await client.GetFoldersAsync(ns, StatusItems.None, false, ct);
            var list = new List<MailFolder>();
            if (!all.Any(f => f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)))
                all = all.Prepend(client.Inbox).ToList();
            foreach (var f in all)
            {
                if ((f.Attributes & FolderAttributes.NonExistent) != 0) continue;
                var parent = f.ParentFolder;
                list.Add(new MailFolder
                {
                    AccountId = accountId,
                    FullName = f.FullName,
                    DisplayName = f == client.Inbox ? "Inbox" : f.Name,
                    ParentFullName = parent == null || string.IsNullOrEmpty(parent.FullName) ? null : parent.FullName,
                    Delimiter = f.DirectorySeparator == '\0' ? "/" : f.DirectorySeparator.ToString(),
                    Special = DetectSpecial(f, client),
                    Selectable = (f.Attributes & FolderAttributes.NoSelect) == 0
                });
            }
            return list;
        }, ct);

        var local = _db.GetFolders(accountId);
        foreach (var f in remote) await _db.UpsertFolderAsync(f);
        var names = remote.Select(r => r.FullName).ToHashSet();
        foreach (var gone in local.Where(l => !names.Contains(l.FullName)))
            await _db.DeleteFolderAsync(gone.Id);
        RaiseFoldersListChanged(accountId);
    }

    public async Task CreateFolderAsync(long accountId, string? parentFullName, string name, CancellationToken ct = default)
    {
        var account = _db.GetAccount(accountId)!;
        if (account.Kind == AccountKind.Local)
        {
            var parent = parentFullName == null ? null : _db.GetFolders(accountId).FirstOrDefault(f => f.FullName == parentFullName);
            await _db.UpsertFolderAsync(new MailFolder
            {
                AccountId = accountId, DisplayName = name, ParentFullName = parentFullName,
                FullName = parent == null ? name : parent.FullName + "/" + name, Delimiter = "/"
            });
            RaiseFoldersListChanged(accountId);
            return;
        }
        await WithClientAsync(accountId, async client =>
        {
            IMailFolder parent = parentFullName == null
                ? await client.GetFolderAsync(client.PersonalNamespaces[0].Path, ct)
                : await client.GetFolderAsync(parentFullName, ct);
            await parent.CreateAsync(name, true, ct);
        }, ct);
        await SyncFolderListAsync(accountId, ct);
    }

    public async Task RenameFolderAsync(long folderId, string newName, CancellationToken ct = default)
    {
        var f = _db.GetFolder(folderId)!;
        var account = _db.GetAccount(f.AccountId)!;
        var newFull = f.ParentFullName == null ? newName : f.ParentFullName + f.Delimiter + newName;
        if (account.Kind != AccountKind.Local)
        {
            await WithClientAsync(f.AccountId, async client =>
            {
                var folder = await client.GetFolderAsync(f.FullName, ct);
                var parent = folder.ParentFolder ?? await client.GetFolderAsync(client.PersonalNamespaces[0].Path, ct);
                await folder.RenameAsync(parent, newName, ct);
            }, ct);
        }
        await _db.RenameFolderAsync(folderId, newFull, newName);
        RaiseFoldersListChanged(f.AccountId);
    }

    public async Task DeleteFolderAsync(long folderId, CancellationToken ct = default)
    {
        var f = _db.GetFolder(folderId)!;
        var account = _db.GetAccount(f.AccountId)!;
        if (account.Kind != AccountKind.Local)
        {
            await WithClientAsync(f.AccountId, async client =>
            {
                var folder = await client.GetFolderAsync(f.FullName, ct);
                await folder.DeleteAsync(ct);
            }, ct);
        }
        var ids = (await _db.GetUidMapAsync(folderId)).Values.ToList();
        foreach (var id in ids) _store.Delete(f.AccountId, id);
        await _db.DeleteFolderAsync(folderId);
        RaiseFoldersListChanged(f.AccountId);
    }

    public async Task EmptyFolderAsync(long folderId, CancellationToken ct = default)
    {
        var f = _db.GetFolder(folderId)!;
        var account = _db.GetAccount(f.AccountId)!;
        if (account.Kind != AccountKind.Local)
        {
            await WithClientAsync(f.AccountId, async client =>
            {
                var folder = await client.GetFolderAsync(f.FullName, ct);
                await folder.OpenAsync(FolderAccess.ReadWrite, ct);
                if (folder.Count > 0)
                {
                    var uids = await folder.SearchAsync(MailKit.Search.SearchQuery.All, ct);
                    await folder.AddFlagsAsync(uids, MailKit.MessageFlags.Deleted, true, ct);
                    await folder.ExpungeAsync(ct);
                }
            }, ct);
        }
        foreach (var id in (await _db.GetUidMapAsync(folderId)).Values) _store.Delete(f.AccountId, id);
        await _db.ClearFolderMessagesAsync(folderId);
        RaiseFolderChanged(folderId, f.AccountId);
    }

    public async Task MarkAllReadAsync(long folderId, CancellationToken ct = default)
    {
        var f = _db.GetFolder(folderId)!;
        var account = _db.GetAccount(f.AccountId)!;
        await _db.MarkAllReadAsync(folderId);
        await _db.RecalcFolderCountsAsync(folderId);
        RaiseFolderChanged(folderId, f.AccountId);
        if (account.Kind == AccountKind.Local) return;
        await WithClientAsync(f.AccountId, async client =>
        {
            var folder = await client.GetFolderAsync(f.FullName, ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            var uids = await folder.SearchAsync(MailKit.Search.SearchQuery.NotSeen, ct);
            if (uids.Count > 0) await folder.AddFlagsAsync(uids, MailKit.MessageFlags.Seen, true, ct);
        }, ct);
    }
}
