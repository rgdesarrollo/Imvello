using System.IO;
using Imvello.Data;
using Imvello.Models;
using MailKit;
using MimeKit;
using LmFlags = Imvello.Models.MessageFlags;
using SpecialFolder = Imvello.Models.SpecialFolder;

namespace Imvello.Services;

public sealed class SendService
{
    private readonly MailDatabase _db;
    private readonly MailClientFactory _clients;
    private readonly SyncService _sync;
    private readonly MessageService _messages;

    public SendService(MailDatabase db, MailClientFactory clients, SyncService sync, MessageService messages)
    {
        _db = db;
        _clients = clients;
        _sync = sync;
        _messages = messages;
    }

    public async Task<MimeMessage> BuildAsync(OutgoingMessage m, CancellationToken ct = default)
    {
        var account = _db.GetAccount(m.AccountId) ?? throw new InvalidOperationException("Choose an account to send from.");
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(account.DisplayName, account.Email));
        foreach (var a in m.To) msg.To.AddRange(InternetAddressList.Parse(a));
        foreach (var a in m.Cc) msg.Cc.AddRange(InternetAddressList.Parse(a));
        foreach (var a in m.Bcc) msg.Bcc.AddRange(InternetAddressList.Parse(a));
        msg.Subject = m.Subject;
        msg.Date = DateTimeOffset.Now;
        if (m.Importance == 2) { msg.Importance = MessageImportance.High; msg.Priority = MessagePriority.Urgent; }
        else if (m.Importance == 0) { msg.Importance = MessageImportance.Low; msg.Priority = MessagePriority.NonUrgent; }

        if (m.ReplyToMessageId is { } rid && !m.IsForward && await _db.GetMessageAsync(rid) is { } orig && orig.MessageId != null)
        {
            msg.InReplyTo = orig.MessageId;
            if (!string.IsNullOrWhiteSpace(orig.References))
                foreach (var r in MimeKit.Utils.MimeUtils.EnumerateReferences(orig.References)) msg.References.Add(r);
            msg.References.Add(orig.MessageId);
        }

        var body = new BodyBuilder { TextBody = HtmlUtil.HtmlToText(m.HtmlBody) };
        var n = 0;
        var html = System.Text.RegularExpressions.Regex.Replace(m.HtmlBody, "src=\"data:(image/[a-zA-Z0-9.+-]+);base64,([^\"]+)\"", match =>
        {
            try
            {
                var bytes = Convert.FromBase64String(match.Groups[2].Value);
                var res = body.LinkedResources.Add($"image{++n}.{match.Groups[1].Value[6..].Split('+')[0]}", bytes, ContentType.Parse(match.Groups[1].Value));
                res.ContentId = MimeKit.Utils.MimeUtils.GenerateMessageId();
                return $"src=\"cid:{res.ContentId}\"";
            }
            catch { return match.Value; }
        });
        body.HtmlBody = "<html><body style=\"font-family:Calibri,'Segoe UI',sans-serif;font-size:11pt\">" + html + "</body></html>";
        foreach (var path in m.AttachmentPaths)
            await body.Attachments.AddAsync(path, ct);
        msg.Body = body.ToMessageBody();
        return msg;
    }

    public async Task SendAsync(OutgoingMessage m, CancellationToken ct = default)
    {
        if (m.To.Count + m.Cc.Count + m.Bcc.Count == 0) throw new InvalidOperationException("There must be at least one name or contact group in the To, Cc, or Bcc box.");
        var account = _db.GetAccount(m.AccountId) ?? throw new InvalidOperationException("Choose an account to send from.");
        var msg = await BuildAsync(m, ct);
        _sync.ReportStatus($"Sending '{m.Subject}'...", null, account.Id);
        using (var smtp = await _clients.ConnectSmtpAsync(account, ct))
        {
            await smtp.SendAsync(msg, ct);
            await smtp.DisconnectAsync(true, ct);
        }

        try
        {
            var recipients = new List<(string Name, string Email)>();
            foreach (var s in m.To.Concat(m.Cc).Concat(m.Bcc))
            {
                if (InternetAddressList.TryParse(s, out var list))
                    foreach (var mb in list.Mailboxes) recipients.Add((mb.Name ?? "", mb.Address));
            }
            await _db.RememberRecipientsAsync(recipients);
        }
        catch { }

        if (account.Kind == AccountKind.Imap && _db.GetSpecialFolder(account.Id, SpecialFolder.Sent) is { } sent)
        {
            try
            {
                await _sync.WithClientAsync(account.Id, async client =>
                {
                    var f = await client.GetFolderAsync(sent.FullName, ct);
                    await f.AppendAsync(new AppendRequest(msg, MailKit.MessageFlags.Seen), ct);
                }, ct);
            }
            catch (Exception ex)
            {
                _sync.ReportStatus("Message sent, but could not be saved to Sent Items: " + ex.Message, null, account.Id);
            }
        }

        if (m.ReplyToMessageId is { } rid)
        {
            try { await _messages.SetBitsAsync([rid], m.IsForward ? LmFlags.Forwarded : LmFlags.Answered, true, ct); } catch { }
        }
        if (m.DraftMessageId is { } did && await _db.GetMessageAsync(did) is { } draft && _db.GetFolder(draft.FolderId) is { } df)
        {
            try { await _messages.PermanentDeleteAsync(df, [draft], ct); } catch { }
        }
        _sync.ReportStatus("Message sent.", null, account.Id);
        if (_db.GetSpecialFolder(account.Id, SpecialFolder.Sent) is { } s2)
            _ = Task.Run(async () => { try { await _sync.SyncFolderAsync(s2.Id, CancellationToken.None); } catch { } });
    }

    public async Task<long?> SaveDraftAsync(OutgoingMessage m, CancellationToken ct = default)
    {
        var account = _db.GetAccount(m.AccountId) ?? throw new InvalidOperationException("Choose an account.");
        var drafts = _db.GetSpecialFolder(account.Id, SpecialFolder.Drafts) ?? throw new InvalidOperationException("This account has no Drafts folder.");
        var msg = await BuildAsync(m, ct);
        UniqueId? uid = null;
        await _sync.WithClientAsync(account.Id, async client =>
        {
            var f = await client.GetFolderAsync(drafts.FullName, ct);
            uid = await f.AppendAsync(new AppendRequest(msg, MailKit.MessageFlags.Seen | MailKit.MessageFlags.Draft), ct);
        }, ct);
        if (m.DraftMessageId is { } did && await _db.GetMessageAsync(did) is { } old)
        {
            try { await _messages.PermanentDeleteAsync(drafts, [old], ct); } catch { }
        }
        await _sync.SyncFolderAsync(drafts.Id, ct);
        if (uid is { } u && (await _db.GetUidMapAsync(drafts.Id)).TryGetValue(u.Id, out var newId)) return newId;
        return null;
    }
}
