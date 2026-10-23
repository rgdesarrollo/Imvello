namespace Imvello.Models;

public class AttachmentInfo
{
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public int Index { get; set; }
}

public class MessageContent
{
    public MessageHeader Header { get; set; } = new();
    public string? HtmlBody { get; set; }
    public string? TextBody { get; set; }
    public List<AttachmentInfo> Attachments { get; set; } = new();
    public string ToFull { get; set; } = "";
    public string CcFull { get; set; } = "";
    public string BccFull { get; set; } = "";
    public string ReplyTo { get; set; } = "";
}

public class OutgoingMessage
{
    public long AccountId { get; set; }
    public List<string> To { get; set; } = new();
    public List<string> Cc { get; set; } = new();
    public List<string> Bcc { get; set; } = new();
    public string Subject { get; set; } = "";
    public string HtmlBody { get; set; } = "";
    public List<string> AttachmentPaths { get; set; } = new();
    public int Importance { get; set; } = 1;
    public long? ReplyToMessageId { get; set; }
    public bool IsForward { get; set; }
    public long? DraftMessageId { get; set; }
}
