using Imvello.Models;
using MailKit;
using MimeKit;
using MailFolder = Imvello.Models.MailFolder;

namespace Imvello.Services;

public sealed partial class MessageService
{
    public async Task MoveAsync(IEnumerable<long> ids, long targetFolderId, CancellationToken ct = default)
    {
        var target = _db.GetFolder(targetFolderId) ?? throw new InvalidOperationException("Target folder not found.");
        foreach (var g in await GroupByFolderAsync(ids))
        {
            if (g.Key == targetFolderId) continue;
            var source = _db.GetFolder(g.Key)!;
            var headers = g.ToList();
            if (source.AccountId == target.AccountId && IsLocal(source.AccountId))
            {
                long next = await _db.GetMaxUidAsync(target.Id) + 1;
                await _db.MoveRowsAsync(target.Id, headers.ToDictionary(h => h.Id, _ => next++));
            }
            else if (source.AccountId == target.AccountId)
            {
                await ServerMoveAsync(source, target, headers, ct);
            }
            else
            {
                await CrossAccountMoveAsync(source, target, headers, ct);
            }
            await RefreshCountsAsync(source.Id, source.AccountId);
            await RefreshCountsAsync(target.Id, target.AccountId);
        }
    }

    private async Task ServerMoveAsync(MailFolder source, MailFolder target, List<MessageHeader> headers, CancellationToken ct)
    {
        var map = await _sync.WithClientAsync(source.AccountId, async client =>
        {
            var src = await client.GetFolderAsync(source.FullName, ct);
            var dst = await client.GetFolderAsync(target.FullName, ct);
            await src.OpenAsync(FolderAccess.ReadWrite, ct);
            var uids = headers.Select(h => new UniqueId((uint)h.Uid)).ToList();
            return await src.MoveToAsync(uids, dst, ct);
        }, ct);

        var moved = new Dictionary<long, long>();
        if (map != null)
        {
            foreach (var h in headers)
                if (map.TryGetValue(new UniqueId((uint)h.Uid), out var nu)) moved[h.Id] = nu.Id;
        }
        if (moved.Count > 0) await _db.MoveRowsAsync(target.Id, moved);
        var rest = headers.Where(h => !moved.ContainsKey(h.Id)).ToList();
        if (rest.Count > 0)
        {
            await _db.DeleteMessagesAsync(rest.Select(h => h.Id));
            foreach (var h in rest) _store.Delete(h.AccountId, h.Id);
            _ = Task.Run(async () =>
            {
                try { await _sync.SyncFolderAsync(target.Id, CancellationToken.None); } catch { }
            });
        }
    }

    private async Task CrossAccountMoveAsync(MailFolder source, MailFolder target, List<MessageHeader> headers, CancellationToken ct)
    {
        var targetLocal = IsLocal(target.AccountId);
        long nextUid = targetLocal ? await _db.GetMaxUidAsync(target.Id) + 1 : 0;
        foreach (var h in headers)
        {
            ct.ThrowIfCancellationRequested();
            var msg = await GetMimeAsync(h.Id, ct);
            if (targetLocal)
            {
                var copy = CloneHeader(h, target.Id, target.AccountId, nextUid++);
                await _db.InsertLocalMessageAsync(copy);
                await _store.SaveAsync(copy.AccountId, copy.Id, msg, ct);
                await _db.SetBodyIndexedAsync(copy.Id, HtmlUtil.ExtractText(msg));
            }
            else
            {
                await _sync.WithClientAsync(target.AccountId, async client =>
                {
                    var dst = await client.GetFolderAsync(target.FullName, ct);
                    await dst.AppendAsync(new AppendRequest(msg, h.Flags.ToMailKit()), ct);
                }, ct);
            }
        }
        await PermanentDeleteAsync(source, headers, ct);
        if (!targetLocal)
        {
            _ = Task.Run(async () =>
            {
                try { await _sync.SyncFolderAsync(target.Id, CancellationToken.None); } catch { }
            });
        }
    }

    private static MessageHeader CloneHeader(MessageHeader h, long folderId, long accountId, long uid) => new()
    {
        FolderId = folderId, AccountId = accountId, Uid = uid, MessageId = h.MessageId, InReplyTo = h.InReplyTo,
        References = h.References, Subject = h.Subject, FromName = h.FromName, FromAddress = h.FromAddress, To = h.To,
        Cc = h.Cc, Date = h.Date, Size = h.Size, Flags = h.Flags, HasAttachments = h.HasAttachments, Preview = h.Preview,
        Importance = h.Importance, BodyDownloaded = true
    };
}
