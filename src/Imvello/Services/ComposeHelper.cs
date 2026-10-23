using Imvello.Data;
using Imvello.Models;
using MimeKit;

namespace Imvello.Services;

public enum ComposeMode { New, Reply, ReplyAll, Forward, EditDraft }

public sealed class ComposeHelper
{
    private readonly MailDatabase _db;
    private readonly MessageService _messages;

    public ComposeHelper(MailDatabase db, MessageService messages)
    {
        _db = db;
        _messages = messages;
    }

    private static string Prefix(string subject, string prefix)
    {
        var s = subject.TrimStart();
        foreach (var p in new[] { "RE:", "FW:", "FWD:", "AW:", "WG:", "RV:", "RES:", "TR:" })
            if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase) && p.StartsWith(prefix[..2], StringComparison.OrdinalIgnoreCase)) return s;
        return prefix + " " + s;
    }

    public string SignatureHtml(long accountId)
    {
        var sig = _db.GetAccount(accountId)?.Signature;
        return string.IsNullOrWhiteSpace(sig) ? "" : "<br><div class=\"signature\">" + sig + "</div>";
    }

    public OutgoingMessage CreateNew(long accountId) => new()
    {
        AccountId = accountId,
        HtmlBody = "<p><br></p>" + SignatureHtml(accountId)
    };

    public async Task<OutgoingMessage> CreateAsync(ComposeMode mode, long messageId, CancellationToken ct = default)
    {
        var h = await _db.GetMessageAsync(messageId) ?? throw new InvalidOperationException("Message not found.");
        var accountId = _db.GetAccount(h.AccountId)?.Kind == AccountKind.Local
            ? _db.GetAccounts().FirstOrDefault(a => a.Kind != AccountKind.Local)?.Id ?? h.AccountId
            : h.AccountId;
        var account = _db.GetAccount(accountId)!;
        var mime = await _messages.GetMimeAsync(messageId, ct);
        var content = await _messages.GetContentAsync(messageId, ct);

        var result = new OutgoingMessage { AccountId = accountId, ReplyToMessageId = messageId };
        if (mode == ComposeMode.EditDraft)
        {
            result.ReplyToMessageId = null;
            result.DraftMessageId = messageId;
            result.To = mime.To.Select(a => a.ToString()).ToList();
            result.Cc = mime.Cc.Select(a => a.ToString()).ToList();
            result.Bcc = mime.Bcc.Select(a => a.ToString()).ToList();
            result.Subject = mime.Subject ?? "";
            result.HtmlBody = content.HtmlBody ?? "";
            result.AttachmentPaths = await _messages.SaveAllAttachmentsToTempAsync(messageId, ct);
            return result;
        }

        var quoteHeader = "<div style=\"border:none;border-top:solid #E1E1E1 1.0pt;padding:3.0pt 0 0 0\"><p style=\"font-family:Calibri,sans-serif;font-size:11pt\">" +
            $"<b>From:</b> {HtmlUtil.Escape(mime.From.ToString())}<br>" +
            $"<b>Sent:</b> {HtmlUtil.Escape(mime.Date.LocalDateTime.ToString("dddd, MMMM d, yyyy h:mm tt"))}<br>" +
            $"<b>To:</b> {HtmlUtil.Escape(mime.To.ToString())}<br>" +
            (mime.Cc.Count > 0 ? $"<b>Cc:</b> {HtmlUtil.Escape(mime.Cc.ToString())}<br>" : "") +
            $"<b>Subject:</b> {HtmlUtil.Escape(mime.Subject)}</p></div>";
        result.HtmlBody = "<p><br></p>" + SignatureHtml(accountId) + "<br>" + quoteHeader + "<div>" + StripHtmlShell(content.HtmlBody ?? "") + "</div>";

        if (mode == ComposeMode.Forward)
        {
            result.IsForward = true;
            result.Subject = Prefix(mime.Subject ?? "", "FW:");
            result.AttachmentPaths = await _messages.SaveAllAttachmentsToTempAsync(messageId, ct);
            return result;
        }

        result.Subject = Prefix(mime.Subject ?? "", "RE:");
        var replyTo = mime.ReplyTo.Count > 0 ? mime.ReplyTo : mime.From;
        result.To = replyTo.Mailboxes.Select(m => m.ToString()).ToList();
        if (mode == ComposeMode.ReplyAll)
        {
            var me = account.Email;
            var seen = new HashSet<string>(replyTo.Mailboxes.Select(m => m.Address), StringComparer.OrdinalIgnoreCase) { me };
            foreach (var m in mime.To.Mailboxes.Where(m => seen.Add(m.Address))) result.To.Add(m.ToString());
            foreach (var m in mime.Cc.Mailboxes.Where(m => seen.Add(m.Address))) result.Cc.Add(m.ToString());
        }
        return result;
    }

    private static string StripHtmlShell(string html)
    {
        var lower = html.ToLowerInvariant();
        var bodyStart = lower.IndexOf("<body", StringComparison.Ordinal);
        if (bodyStart < 0) return html;
        var gt = lower.IndexOf('>', bodyStart);
        var end = lower.LastIndexOf("</body>", StringComparison.Ordinal);
        return gt > 0 && end > gt ? html[(gt + 1)..end] : html;
    }
}
