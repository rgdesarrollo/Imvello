using System.Collections.Concurrent;
using Imvello.Models;
using MailKit;
using SpecialFolder = Imvello.Models.SpecialFolder;
using MailFolder = Imvello.Models.MailFolder;
using MailKit.Search;
using MimeKit;

namespace Imvello.Services;

public sealed partial class SyncService
{
    private const int HeaderBatch = 250;
    private readonly ConcurrentDictionary<long, Task<MimeMessage>> _bodyFetches = new();
    private static readonly HashSet<string> ExtraHeaders = new(StringComparer.OrdinalIgnoreCase) { "Importance", "X-Priority" };

    public async Task SyncFolderAsync(long folderId, CancellationToken ct)
    {
        var f = _db.GetFolder(folderId);
        if (f == null || !f.Selectable) return;
        var account = _db.GetAccount(f.AccountId);
        if (account == null || account.Kind == AccountKind.Local) return;

        await WithClientAsync(f.AccountId, async client =>
        {
            var folder = await client.GetFolderAsync(f.FullName, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            long uidValidity = folder.UidValidity;
            if (f.UidValidity != 0 && f.UidValidity != uidValidity)
            {
                await _db.ClearFolderMessagesAsync(folderId);
                f.LastUid = 0;
                f.HighestModSeq = 0;
            }

            var localMap = await _db.GetUidMapAsync(folderId);
            var serverUids = folder.Count == 0 ? new List<UniqueId>() : (await folder.SearchAsync(SearchQuery.All, ct)).ToList();
            var serverSet = new HashSet<long>(serverUids.Select(u => (long)u.Id));

            var removed = localMap.Keys.Where(u => !serverSet.Contains(u)).ToList();
            if (removed.Count > 0)
            {
                foreach (var u in removed) _store.Delete(f.AccountId, localMap[u]);
                await _db.DeleteByUidsAsync(folderId, removed);
            }

            var known = serverUids.Where(u => localMap.ContainsKey(u.Id)).ToList();
            if (known.Count > 0)
            {
                bool condstore = folder.Supports(FolderFeature.ModSequences) && f.HighestModSeq > 0;
                if (!condstore || folder.HighestModSeq != (ulong)f.HighestModSeq)
                {
                    foreach (var chunk in known.Chunk(2000))
                    {
                        ct.ThrowIfCancellationRequested();
                        IList<IMessageSummary> sums = condstore
                            ? await folder.FetchAsync(chunk, (ulong)f.HighestModSeq, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct)
                            : await folder.FetchAsync(chunk, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct);
                        var dict = sums.ToDictionary(s => (long)s.UniqueId.Id, s => FlagMapper.FromMailKit(s.Flags, s.Keywords));
                        await _db.UpdateFlagsByUidAsync(folderId, dict);
                    }
                }
            }

            var newUids = serverUids.Where(u => !localMap.ContainsKey(u.Id)).OrderByDescending(u => u.Id).ToList();
            int done = 0;
            foreach (var chunk in newUids.Chunk(HeaderBatch))
            {
                ct.ThrowIfCancellationRequested();
                ReportStatus($"Receiving {done + chunk.Length:N0} of {newUids.Count:N0} in {f.DisplayName} ({account.Email})",
                    newUids.Count == 0 ? null : (double)done / newUids.Count, f.AccountId);
                var request = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags |
                    MessageSummaryItems.Size | MessageSummaryItems.BodyStructure | MessageSummaryItems.InternalDate |
                    MessageSummaryItems.PreviewText | MessageSummaryItems.References, ExtraHeaders);
                var sums = await folder.FetchAsync(chunk, request, ct);
                var headers = sums.Select(s => ToHeader(s, f.Id, f.AccountId)).ToList();
                await _db.UpsertHeadersAsync(headers);
                done += chunk.Length;
                await _db.RecalcFolderCountsAsync(folderId);
                RaiseFolderChanged(folderId, f.AccountId);
            }

            var maxUid = serverUids.Count == 0 ? 0 : serverUids.Max(u => (long)u.Id);
            await _db.UpdateFolderSyncStateAsync(folderId, uidValidity, maxUid, (long)folder.HighestModSeq);
            await _db.RecalcFolderCountsAsync(folderId);
            if (removed.Count > 0 || known.Count > 0 || newUids.Count == 0) RaiseFolderChanged(folderId, f.AccountId);
        }, ct);
    }

    private static MessageHeader ToHeader(IMessageSummary s, long folderId, long accountId)
    {
        var env = s.Envelope;
        var from = env?.From.Mailboxes.FirstOrDefault();
        var importance = 1;
        if (s.Headers != null)
        {
            var imp = s.Headers["Importance"]?.Trim().ToLowerInvariant();
            var pri = s.Headers["X-Priority"]?.Trim();
            if (imp == "high" || pri?.StartsWith('1') == true || pri?.StartsWith('2') == true) importance = 2;
            else if (imp == "low" || pri?.StartsWith('4') == true || pri?.StartsWith('5') == true) importance = 0;
        }
        return new MessageHeader
        {
            FolderId = folderId,
            AccountId = accountId,
            Uid = s.UniqueId.Id,
            MessageId = env?.MessageId,
            InReplyTo = env?.InReplyTo,
            References = s.References == null ? null : string.Join(" ", s.References.Select(r => "<" + r + ">")),
            Subject = env?.Subject ?? "",
            FromName = from?.Name ?? "",
            FromAddress = from?.Address ?? "",
            To = FormatList(env?.To),
            Cc = FormatList(env?.Cc),
            Date = env?.Date ?? s.InternalDate ?? DateTimeOffset.Now,
            Size = s.Size ?? 0,
            Flags = FlagMapper.FromMailKit(s.Flags, s.Keywords),
            HasAttachments = s.Attachments?.Any() == true,
            Preview = (s.PreviewText ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim(),
            Importance = importance
        };
    }

    public static string FormatList(InternetAddressList? list) =>
        list == null ? "" : string.Join("; ", list.Mailboxes.Select(m => string.IsNullOrWhiteSpace(m.Name) ? m.Address : m.Name));

    private async Task DownloadBodiesAsync(long accountId, List<MailFolder> folders, CancellationToken ct)
    {
        foreach (var f in folders)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var ids = await _db.GetIdsWithoutBodyAsync(f.Id, 50);
                if (ids.Count == 0) break;
                ReportStatus($"Downloading messages in {f.DisplayName}...", null, accountId);
                foreach (var h in await _db.GetMessagesAsync(ids))
                {
                    try { await EnsureBodyAsync(h, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch { await _db.SetBodyIndexedAsync(h.Id, h.Preview); }
                }
            }
        }
    }

    public async Task<MimeMessage> EnsureBodyAsync(MessageHeader h, CancellationToken ct, bool interactive = false)
    {
        var cached = await _store.LoadAsync(h.AccountId, h.Id, ct);
        if (cached != null) return cached;
        var task = _bodyFetches.GetOrAdd(h.Id, _ => Task.Run(() => FetchBodyAsync(h, interactive)));
        return await task.WaitAsync(ct);
    }

    private async Task<MimeMessage> FetchBodyAsync(MessageHeader h, bool interactive)
    {
        try
        {
            var f = _db.GetFolder(h.FolderId) ?? throw new InvalidOperationException("Folder not found.");
            var msg = await WithClientAsync(h.AccountId, async client =>
            {
                var folder = await client.GetFolderAsync(f.FullName, CancellationToken.None);
                if (!folder.IsOpen) await folder.OpenAsync(FolderAccess.ReadOnly, CancellationToken.None);
                return await folder.GetMessageAsync(new UniqueId((uint)h.Uid), CancellationToken.None);
            }, CancellationToken.None, interactive);
            await _store.SaveAsync(h.AccountId, h.Id, msg, CancellationToken.None);
            await _db.SetBodyIndexedAsync(h.Id, HtmlUtil.ExtractText(msg));
            return msg;
        }
        finally { _bodyFetches.TryRemove(h.Id, out _); }
    }
}
