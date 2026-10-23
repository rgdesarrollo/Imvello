namespace Imvello.Models;

public class MessageHeader
{
    public long Id { get; set; }
    public long FolderId { get; set; }
    public long AccountId { get; set; }
    public long Uid { get; set; }
    public string? MessageId { get; set; }
    public string? InReplyTo { get; set; }
    public string? References { get; set; }
    public string Subject { get; set; } = "";
    public string FromName { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public DateTimeOffset Date { get; set; }
    public long Size { get; set; }
    public MessageFlags Flags { get; set; }
    public bool HasAttachments { get; set; }
    public string Preview { get; set; } = "";
    public int Importance { get; set; } = 1;
    public bool BodyDownloaded { get; set; }

    public bool IsRead => Flags.HasFlag(MessageFlags.Seen);
    public bool IsFlagged => Flags.HasFlag(MessageFlags.Flagged);
    public string Sender => string.IsNullOrWhiteSpace(FromName) ? FromAddress : FromName;
}
