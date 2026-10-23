using System.IO;
using Imvello.Data;
using Imvello.Models;
using MimeKit;

namespace Imvello.Services;

public sealed partial class MessageService
{
    private const int CacheCapacity = 40;
    private readonly object _cacheLock = new();
    private readonly Dictionary<long, LinkedListNode<(long Id, MessageContent Content)>> _cache = new();
    private readonly LinkedList<(long Id, MessageContent Content)> _cacheList = new();
    private readonly MailDatabase _db;
    private readonly MessageStore _store;
    private readonly SyncService _sync;

    public MessageService(MailDatabase db, MessageStore store, SyncService sync)
    {
        _db = db;
        _store = store;
        _sync = sync;
    }

    public async Task<MimeMessage> GetMimeAsync(long messageId, CancellationToken ct = default)
    {
        var h = await _db.GetMessageAsync(messageId) ?? throw new InvalidOperationException("Message not found.");
        return await GetMimeAsync(h, ct);
    }

    private async Task<MimeMessage> GetMimeAsync(MessageHeader h, CancellationToken ct)
    {
        var account = _db.GetAccount(h.AccountId);
        if (account?.Kind == AccountKind.Local)
            return await _store.LoadAsync(h.AccountId, h.Id, ct) ?? throw new FileNotFoundException("Message file is missing from the local store.");
        return await _sync.EnsureBodyAsync(h, ct, interactive: true);
    }

    public async Task<MessageContent> GetContentAsync(long messageId, CancellationToken ct = default)
    {
        if (TryGetCachedContent(messageId) is { } hit) return hit;
        var h = await _db.GetMessageAsync(messageId) ?? throw new InvalidOperationException("Message not found.");
        var msg = await GetMimeAsync(h, ct);
        var content = new MessageContent
        {
            Header = h,
            TextBody = msg.TextBody,
            ToFull = msg.To.ToString(),
            CcFull = msg.Cc.ToString(),
            BccFull = msg.Bcc.ToString(),
            ReplyTo = msg.ReplyTo.ToString()
        };
        var html = msg.HtmlBody;
        if (html != null)
        {
            foreach (var part in msg.BodyParts.OfType<MimePart>().Where(p => !string.IsNullOrEmpty(p.ContentId)))
            {
                var cid = "cid:" + part.ContentId!.Trim('<', '>');
                if (!html.Contains(cid, StringComparison.OrdinalIgnoreCase)) continue;
                using var ms = new MemoryStream();
                await part.Content.DecodeToAsync(ms, ct);
                html = html.Replace(cid, $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(ms.ToArray())}", StringComparison.OrdinalIgnoreCase);
            }
        }
        content.HtmlBody = html ?? HtmlUtil.TextToHtml(msg.TextBody ?? "");
        int i = 0;
        foreach (var att in msg.Attachments)
        {
            var name = att is MimePart mp ? mp.FileName : (att as MessagePart)?.Message?.Subject + ".eml";
            content.Attachments.Add(new AttachmentInfo
            {
                Index = i++,
                FileName = string.IsNullOrWhiteSpace(name) ? $"attachment{i}" : name!,
                ContentType = att.ContentType.MimeType,
                Size = att is MimePart p2 && p2.Content?.Stream is { CanSeek: true } s ? (long)(s.Length * (p2.ContentTransferEncoding == ContentEncoding.Base64 ? 0.75 : 1)) : 0
            });
        }
        CacheContent(content);
        return content;
    }

    public MessageContent? TryGetCachedContent(long messageId)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(messageId, out var node))
            {
                _cacheList.Remove(node);
                _cacheList.AddFirst(node);
                return node.Value.Content;
            }
            return null;
        }
    }

    public void InvalidateContent(long messageId)
    {
        lock (_cacheLock)
        {
            if (_cache.Remove(messageId, out var node)) _cacheList.Remove(node);
        }
    }

    private void CacheContent(MessageContent c)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(c.Header.Id, out var node)) _cacheList.Remove(node);
            _cache[c.Header.Id] = _cacheList.AddFirst((c.Header.Id, c));
            while (_cacheList.Count > CacheCapacity)
            {
                var tail = _cacheList.Last!;
                _cacheList.RemoveLast();
                _cache.Remove(tail.Value.Id);
            }
        }
    }

    public bool IsBodyLocal(MessageHeader h) => _store.Exists(h.AccountId, h.Id);

    public async Task PrefetchContentAsync(IEnumerable<MessageHeader> headers, int networkCount = 0)
    {
        var n = 0;
        foreach (var h in headers)
        {
            var allowNetwork = n++ < networkCount && !_sync.WorkOffline;
            if (TryGetCachedContent(h.Id) != null || (!allowNetwork && !IsBodyLocal(h))) continue;
            try { await GetContentAsync(h.Id); }
            catch { }
        }
    }

    public async Task SaveAttachmentAsync(long messageId, int index, string path, CancellationToken ct = default)
    {
        var msg = await GetMimeAsync(messageId, ct);
        var att = msg.Attachments.ElementAt(index);
        await using var fs = File.Create(path);
        if (att is MessagePart mp) await mp.Message.WriteToAsync(fs, ct);
        else if (att is MimePart p) await p.Content.DecodeToAsync(fs, ct);
    }

    public async Task<List<string>> SaveAllAttachmentsToTempAsync(long messageId, CancellationToken ct = default)
    {
        var msg = await GetMimeAsync(messageId, ct);
        var dir = Path.Combine(Path.GetTempPath(), "Imvello", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var result = new List<string>();
        int i = 0;
        foreach (var att in msg.Attachments)
        {
            var name = (att as MimePart)?.FileName ?? $"attachment{i}.eml";
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var path = Path.Combine(dir, name);
            await using (var fs = File.Create(path))
            {
                if (att is MessagePart mp) await mp.Message.WriteToAsync(fs, ct);
                else if (att is MimePart p) await p.Content.DecodeToAsync(fs, ct);
            }
            result.Add(path);
            i++;
        }
        return result;
    }

    public async Task SaveMessageAsEmlAsync(long messageId, string path, CancellationToken ct = default)
    {
        var msg = await GetMimeAsync(messageId, ct);
        await using var fs = File.Create(path);
        await msg.WriteToAsync(fs, ct);
    }
}
