using System.IO;
using System.Text;
using Imvello.Data;
using Imvello.Models;
using MailKit;
using MimeKit;
using LmFlags = Imvello.Models.MessageFlags;
using MailFolder = Imvello.Models.MailFolder;

namespace Imvello.Services;

public sealed class MboxService
{
    private readonly MailDatabase _db;
    private readonly MessageStore _store;
    private readonly SyncService _sync;
    private readonly MessageService _messages;

    public MboxService(MailDatabase db, MessageStore store, SyncService sync, MessageService messages)
    {
        _db = db;
        _store = store;
        _sync = sync;
        _messages = messages;
    }

    public static bool LooksLikeMbox(string path)
    {
        try
        {
            if (new FileInfo(path).Length == 0) return File.Exists(path + ".msf");
            using var fs = File.OpenRead(path);
            var buf = new byte[5];
            return fs.Read(buf, 0, 5) == 5 && Encoding.ASCII.GetString(buf) == "From ";
        }
        catch { return false; }
    }

    public async Task<long> ImportMboxAsync(string mboxPath, long targetFolderId, IProgress<string>? progress, CancellationToken ct)
    {
        var target = _db.GetFolder(targetFolderId) ?? throw new InvalidOperationException("Target folder not found.");
        var local = _db.GetAccount(target.AccountId)?.Kind == AccountKind.Local;
        long nextUid = await _db.GetMaxUidAsync(targetFolderId) + 1;
        long count = 0;
        var batch = new List<(MessageHeader h, MimeMessage m)>();

        await using var fs = new FileStream(mboxPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        var parser = new MimeParser(fs, MimeFormat.Mbox);
        while (!parser.IsEndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            MimeMessage msg;
            try { msg = await parser.ParseMessageAsync(ct); }
            catch (FormatException) { break; }

            if (local)
            {
                batch.Add((ToHeader(msg, target, nextUid++), msg));
                if (batch.Count >= 500) await FlushAsync(batch, ct);
            }
            else
            {
                await _sync.WithClientAsync(target.AccountId, async client =>
                {
                    var f = await client.GetFolderAsync(target.FullName, ct);
                    await f.AppendAsync(new AppendRequest(msg, ParseMozillaStatus(msg).ToMailKit()), ct);
                }, ct);
            }
            count++;
            if (count % 100 == 0) progress?.Report($"Importing {Path.GetFileName(mboxPath)}: {count:N0} messages ({fs.Position * 100 / Math.Max(1, fs.Length)}%)");
        }
        if (batch.Count > 0) await FlushAsync(batch, ct);
        await _db.RecalcFolderCountsAsync(targetFolderId);
        _sync.RaiseFolderChanged(targetFolderId, target.AccountId);
        if (!local) await _sync.SyncFolderAsync(targetFolderId, ct);
        progress?.Report($"Imported {count:N0} messages from {Path.GetFileName(mboxPath)}");
        return count;
    }

    private async Task FlushAsync(List<(MessageHeader h, MimeMessage m)> batch, CancellationToken ct)
    {
        await _db.UpsertHeadersAsync(batch.Select(b => b.h).ToList());
        foreach (var (h, m) in batch)
        {
            await _store.SaveAsync(h.AccountId, h.Id, m, ct);
            await _db.SetBodyIndexedAsync(h.Id, HtmlUtil.ExtractText(m));
        }
        batch.Clear();
    }

    private static LmFlags ParseMozillaStatus(MimeMessage msg)
    {
        var f = LmFlags.None;
        var s = msg.Headers["X-Mozilla-Status"];
        if (s != null && int.TryParse(s.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var v))
        {
            if ((v & 0x1) != 0) f |= LmFlags.Seen;
            if ((v & 0x2) != 0) f |= LmFlags.Answered;
            if ((v & 0x4) != 0) f |= LmFlags.Flagged;
            if ((v & 0x1000) != 0) f |= LmFlags.Forwarded;
        }
        else f |= LmFlags.Seen;
        return f;
    }

    private static MessageHeader ToHeader(MimeMessage msg, MailFolder target, long uid)
    {
        var from = msg.From.Mailboxes.FirstOrDefault();
        var text = HtmlUtil.ExtractText(msg);
        return new MessageHeader
        {
            FolderId = target.Id, AccountId = target.AccountId, Uid = uid, MessageId = msg.MessageId, InReplyTo = msg.InReplyTo,
            References = msg.References.Count > 0 ? string.Join(" ", msg.References.Select(r => "<" + r + ">")) : null,
            Subject = msg.Subject ?? "", FromName = from?.Name ?? "", FromAddress = from?.Address ?? "",
            To = SyncService.FormatList(msg.To), Cc = SyncService.FormatList(msg.Cc), Date = msg.Date,
            Size = 0, Flags = ParseMozillaStatus(msg), HasAttachments = msg.Attachments.Any(),
            Preview = text.Length > 200 ? text[..200].Replace('\n', ' ').Replace('\r', ' ') : text.Replace('\n', ' ').Replace('\r', ' '),
            Importance = msg.Importance == MessageImportance.High ? 2 : msg.Importance == MessageImportance.Low ? 0 : 1,
            BodyDownloaded = true
        };
    }

    public async Task<long> ImportThunderbirdDirectoryAsync(string directory, long localAccountId, string? parentFullName, IProgress<string>? progress, CancellationToken ct)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory).Where(f => Path.GetExtension(f).Length == 0 || Path.GetExtension(f).Length > 5).OrderBy(f => f))
        {
            ct.ThrowIfCancellationRequested();
            if (!LooksLikeMbox(file)) continue;
            var name = Path.GetFileName(file);
            var full = parentFullName == null ? name : parentFullName + "/" + name;
            var folder = new MailFolder { AccountId = localAccountId, FullName = full, DisplayName = name, ParentFullName = parentFullName, Delimiter = "/" };
            await _db.UpsertFolderAsync(folder);
            total += await ImportMboxAsync(file, folder.Id, progress, ct);
            var sbd = file + ".sbd";
            if (Directory.Exists(sbd)) total += await ImportThunderbirdDirectoryAsync(sbd, localAccountId, full, progress, ct);
        }
        _sync.RaiseFoldersListChanged(localAccountId);
        return total;
    }

    public async Task<long> ExportFolderToMboxAsync(long folderId, string path, IProgress<string>? progress, CancellationToken ct)
    {
        long count = 0, after = 0;
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Unix;
        while (true)
        {
            var page = await _db.GetFolderMessagesForExportAsync(folderId, after, 200);
            if (page.Count == 0) break;
            foreach (var h in page)
            {
                ct.ThrowIfCancellationRequested();
                after = h.Id;
                MimeMessage msg;
                try { msg = await _messages.GetMimeAsync(h.Id, ct); }
                catch (OperationCanceledException) { throw; }
                catch { continue; }
                var sep = Encoding.ASCII.GetBytes("From - " + h.Date.UtcDateTime.ToString("ddd MMM dd HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture) + "\n");
                await fs.WriteAsync(sep, ct);
                using (var filtered = new MimeKit.IO.FilteredStream(fs))
                {
                    filtered.Add(new MimeKit.IO.Filters.MboxFromFilter());
                    await msg.WriteToAsync(options, filtered, true, ct);
                    await filtered.FlushAsync(ct);
                }
                await fs.WriteAsync("\n"u8.ToArray(), ct);
                count++;
                if (count % 100 == 0) progress?.Report($"Exported {count:N0} messages...");
            }
        }
        progress?.Report($"Exported {count:N0} messages to {Path.GetFileName(path)}");
        return count;
    }
}
