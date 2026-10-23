namespace Imvello.Models;

public class MailFolder
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public string FullName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? ParentFullName { get; set; }
    public string Delimiter { get; set; } = "/";
    public SpecialFolder Special { get; set; }
    public bool Selectable { get; set; } = true;
    public long UidValidity { get; set; }
    public long LastUid { get; set; }
    public long HighestModSeq { get; set; }
    public int UnreadCount { get; set; }
    public int TotalCount { get; set; }
}
