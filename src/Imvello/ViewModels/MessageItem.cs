using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Imvello.Models;
using Imvello.Services;

namespace Imvello.ViewModels;

public partial class MessageItem : ObservableObject
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x0F, 0x6C, 0xBD), Color.FromRgb(0xC2, 0x39, 0xB3), Color.FromRgb(0x13, 0xA1, 0x0E), Color.FromRgb(0xCA, 0x50, 0x10),
        Color.FromRgb(0x86, 0x64, 0xB8), Color.FromRgb(0x03, 0x83, 0x87), Color.FromRgb(0xD1, 0x34, 0x38), Color.FromRgb(0x4F, 0x6B, 0xED)
    ];

    public MessageHeader Header { get; }
    public bool ShowRecipient { get; }

    public MessageItem(MessageHeader header, bool showRecipient = false)
    {
        Header = header;
        ShowRecipient = showRecipient;
        _isRead = header.IsRead;
        _isFlagged = header.IsFlagged;
    }

    [ObservableProperty] private bool _isRead;
    [ObservableProperty] private bool _isFlagged;

    public long Id => Header.Id;
    public string Sender => ShowRecipient ? (string.IsNullOrWhiteSpace(Header.To) ? Loc.T("Main.NoRecipient") : Header.To) : (string.IsNullOrWhiteSpace(Header.Sender) ? Loc.T("Main.UnknownSender") : Header.Sender);
    public string Subject => string.IsNullOrWhiteSpace(Header.Subject) ? Loc.T("Main.NoSubject") : Header.Subject;
    public string Preview => Header.Preview;
    public bool HasAttachments => Header.HasAttachments;
    public bool IsHighImportance => Header.Importance == 2;
    public bool IsAnswered => Header.Flags.HasFlag(MessageFlags.Answered);
    public bool IsForwarded => Header.Flags.HasFlag(MessageFlags.Forwarded);

    public string DateText
    {
        get
        {
            var d = Header.Date.LocalDateTime;
            var today = DateTime.Today;
            if (d.Date == today) return d.ToString("h:mm tt");
            if (d.Date > today.AddDays(-7)) return d.ToString("ddd h:mm tt");
            if (d.Year == today.Year) return d.ToString("ddd M/d");
            return d.ToString("M/d/yyyy");
        }
    }

    public string GroupName
    {
        get
        {
            var d = Header.Date.LocalDateTime.Date;
            var today = DateTime.Today;
            if (d == today) return Loc.T("Main.Today");
            if (d == today.AddDays(-1)) return Loc.T("Main.Yesterday");
            var startOfWeek = today.AddDays(-(int)today.DayOfWeek);
            if (d >= startOfWeek) return d.DayOfWeek.ToString();
            if (d >= startOfWeek.AddDays(-7)) return Loc.T("Main.LastWeek");
            if (d >= startOfWeek.AddDays(-14)) return Loc.T("Main.TwoWeeksAgo");
            if (d >= new DateTime(today.Year, today.Month, 1)) return Loc.T("Main.EarlierThisMonth");
            if (d >= new DateTime(today.Year, today.Month, 1).AddMonths(-1)) return Loc.T("Main.LastMonth");
            return Loc.T("Main.Older");
        }
    }

    public string Initials
    {
        get
        {
            var parts = Sender.Split([' ', '.', '_', '@'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            if (parts.Length == 1 || Sender.Contains('@')) return parts[0][..1].ToUpperInvariant();
            return (parts[0][..1] + parts[^1][..1]).ToUpperInvariant();
        }
    }

    public Brush AvatarBrush
    {
        get
        {
            var key = string.IsNullOrEmpty(Header.FromAddress) ? Sender : Header.FromAddress;
            uint hash = 17;
            foreach (var ch in key.ToLowerInvariant()) hash = unchecked(hash * 31 + ch);
            var b = new SolidColorBrush(Palette[(int)(hash % (uint)Palette.Length)]);
            b.Freeze();
            return b;
        }
    }

    partial void OnIsReadChanged(bool value) =>
        Header.Flags = value ? Header.Flags | MessageFlags.Seen : Header.Flags & ~MessageFlags.Seen;

    partial void OnIsFlaggedChanged(bool value) =>
        Header.Flags = value ? Header.Flags | MessageFlags.Flagged : Header.Flags & ~MessageFlags.Flagged;
}
