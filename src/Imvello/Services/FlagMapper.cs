using MailKit;
using LmFlags = Imvello.Models.MessageFlags;

namespace Imvello.Services;

public static class FlagMapper
{
    public static MessageFlags ToMailKit(this LmFlags f)
    {
        var r = MessageFlags.None;
        if (f.HasFlag(LmFlags.Seen)) r |= MessageFlags.Seen;
        if (f.HasFlag(LmFlags.Answered)) r |= MessageFlags.Answered;
        if (f.HasFlag(LmFlags.Flagged)) r |= MessageFlags.Flagged;
        if (f.HasFlag(LmFlags.Deleted)) r |= MessageFlags.Deleted;
        if (f.HasFlag(LmFlags.Draft)) r |= MessageFlags.Draft;
        return r;
    }

    public static LmFlags FromMailKit(MessageFlags? f, IEnumerable<string>? keywords)
    {
        var r = LmFlags.None;
        if (f is { } v)
        {
            if (v.HasFlag(MessageFlags.Seen)) r |= LmFlags.Seen;
            if (v.HasFlag(MessageFlags.Answered)) r |= LmFlags.Answered;
            if (v.HasFlag(MessageFlags.Flagged)) r |= LmFlags.Flagged;
            if (v.HasFlag(MessageFlags.Deleted)) r |= LmFlags.Deleted;
            if (v.HasFlag(MessageFlags.Draft)) r |= LmFlags.Draft;
        }
        if (keywords != null && keywords.Contains("$Forwarded")) r |= LmFlags.Forwarded;
        return r;
    }
}
