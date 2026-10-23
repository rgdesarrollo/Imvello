using Imvello.Models;

namespace Imvello.Services;

public static class MailProviderDefaults
{
    public static void Apply(Account a)
    {
        switch (a.Kind)
        {
            case AccountKind.Gmail:
                a.ImapHost = "imap.gmail.com"; a.ImapPort = 993; a.ImapSecurity = SecurityMode.SslOnConnect;
                a.SmtpHost = "smtp.gmail.com"; a.SmtpPort = 465; a.SmtpSecurity = SecurityMode.SslOnConnect;
                break;
            case AccountKind.Outlook:
                a.ImapHost = "outlook.office365.com"; a.ImapPort = 993; a.ImapSecurity = SecurityMode.SslOnConnect;
                a.SmtpHost = "smtp-mail.outlook.com"; a.SmtpPort = 587; a.SmtpSecurity = SecurityMode.StartTls;
                break;
            case AccountKind.Imap:
                var domain = a.Email.Contains('@') ? a.Email[(a.Email.IndexOf('@') + 1)..] : "";
                if (string.IsNullOrWhiteSpace(a.ImapHost)) a.ImapHost = "imap." + domain;
                if (string.IsNullOrWhiteSpace(a.SmtpHost)) a.SmtpHost = "smtp." + domain;
                break;
        }
        if (string.IsNullOrWhiteSpace(a.UserName)) a.UserName = a.Email;
    }

    public static AccountKind DetectKind(string email)
    {
        var e = email.Trim().ToLowerInvariant();
        if (e.EndsWith("@gmail.com") || e.EndsWith("@googlemail.com")) return AccountKind.Gmail;
        if (e.EndsWith("@outlook.com") || e.EndsWith("@hotmail.com") || e.EndsWith("@live.com") || e.EndsWith("@msn.com"))
            return AccountKind.Outlook;
        return AccountKind.Imap;
    }
}
