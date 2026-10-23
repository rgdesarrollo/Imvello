namespace Imvello.Models;

public enum AccountKind { Local, Gmail, Outlook, Imap }

public enum AuthType { Password, OAuth2 }

public enum SecurityMode { None, SslOnConnect, StartTls, Auto }

public enum SpecialFolder { None, Inbox, Sent, Drafts, Trash, Junk, Archive, All, Outbox }

[Flags]
public enum MessageFlags { None = 0, Seen = 1, Answered = 2, Flagged = 4, Deleted = 8, Draft = 16, Forwarded = 32 }
