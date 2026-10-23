namespace Imvello.Models;

public class Account
{
    public long Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public AccountKind Kind { get; set; }
    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public SecurityMode ImapSecurity { get; set; } = SecurityMode.SslOnConnect;
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public SecurityMode SmtpSecurity { get; set; } = SecurityMode.StartTls;
    public string UserName { get; set; } = "";
    public AuthType AuthType { get; set; }
    public string? Signature { get; set; }
    public string? Color { get; set; }
    public int SortOrder { get; set; }
    public bool Enabled { get; set; } = true;
}
