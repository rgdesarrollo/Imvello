using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Imvello.Models;
using Imvello.Services;

namespace Imvello.ViewModels;

public partial class FolderNode : ObservableObject
{
    public Account Account { get; }
    public MailFolder? Folder { get; set; }
    public bool IsAccountNode => Folder == null;
    public bool IsFavorite { get; init; }
    public ObservableCollection<FolderNode> Children { get; } = new();

    [ObservableProperty] private string _name;
    [ObservableProperty] private int _unreadCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public FolderNode(Account account, MailFolder? folder, string name)
    {
        Account = account;
        Folder = folder;
        _name = name;
        if (folder != null)
        {
            _unreadCount = folder.UnreadCount;
            _totalCount = folder.TotalCount;
        }
    }

    public string BadgeText => Folder?.Special is SpecialFolder.Drafts or SpecialFolder.Outbox
        ? (TotalCount > 0 ? TotalCount.ToString() : "")
        : (UnreadCount > 0 ? UnreadCount.ToString() : "");

    public bool IsBold => UnreadCount > 0 && Folder?.Special is not (SpecialFolder.Sent or SpecialFolder.Trash or SpecialFolder.All);

    partial void OnUnreadCountChanged(int value)
    {
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(IsBold));
    }

    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(BadgeText));

    public string Icon => Folder == null ? (Account.Kind == AccountKind.Local ? "\uE8B7" : "\uE77B") : Folder.Special switch
    {
        SpecialFolder.Inbox => "\uE715",
        SpecialFolder.Sent => "\uE724",
        SpecialFolder.Drafts => "\uE70F",
        SpecialFolder.Trash => "\uE74D",
        SpecialFolder.Junk => "\uE8F8",
        SpecialFolder.Archive => "\uE7B8",
        SpecialFolder.All => "\uE8F1",
        SpecialFolder.Outbox => "\uE8B5",
        _ => "\uE8B7"
    };

    public static string FriendlyName(MailFolder f) => f.Special switch
    {
        SpecialFolder.Inbox => Loc.T("Main.InboxFolder"),
        SpecialFolder.Sent => Loc.T("Main.SentItems"),
        SpecialFolder.Drafts => Loc.T("Main.Drafts"),
        SpecialFolder.Trash => Loc.T("Main.DeletedItems"),
        SpecialFolder.Junk => Loc.T("Main.JunkEmail"),
        SpecialFolder.Archive => Loc.T("Main.ArchiveFolder"),
        SpecialFolder.Outbox => Loc.T("Main.Outbox"),
        _ => f.DisplayName == "INBOX" ? Loc.T("Main.InboxFolder") : f.DisplayName
    };

    public string? NameSuffix { get; init; }

    public static string AccountName(Account a) =>
        a.Kind != AccountKind.Local ? a.Email :
        a.DisplayName == AccountService.LocalFoldersName ? Loc.T("Main.LocalFolders") : a.DisplayName;

    public void RefreshDisplayName()
    {
        if (Folder != null) Name = FriendlyName(Folder) + (NameSuffix ?? "");
        else Name = AccountName(Account);
    }

    public static int SortKey(MailFolder f) => f.Special switch
    {
        SpecialFolder.Inbox => 0,
        SpecialFolder.Drafts => 1,
        SpecialFolder.Sent => 2,
        SpecialFolder.Trash => 3,
        SpecialFolder.Junk => 4,
        SpecialFolder.Archive => 5,
        SpecialFolder.Outbox => 6,
        SpecialFolder.All => 8,
        _ => 7
    };
}
