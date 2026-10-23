using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Imvello.Models;
using Imvello.Services;

namespace Imvello.ViewModels;

public partial class MainViewModel : ObservableObject, IReadingSource
{
    private const int PageSize = 300;
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private readonly HashSet<long> _pendingFolderRefresh = new();
    private CancellationTokenSource? _contentCts;
    private CancellationTokenSource? _syncCts;
    private int _listVersion;
    private bool _loadingMore;

    public ObservableCollection<FolderNode> Favorites { get; } = new();
    public ObservableCollection<FolderNode> FolderTree { get; } = new();
    public ObservableCollection<MessageItem> Messages { get; } = new();
    public List<MessageItem> SelectedItems { get; private set; } = new();
    public AppSettings Settings => AppServices.Settings;

    [ObservableProperty] private FolderNode? _selectedNode;
    [ObservableProperty] private MessageItem? _currentItem;
    [ObservableProperty] private MessageContent? _currentContent;
    [ObservableProperty] private bool _isLoadingContent;
    [ObservableProperty] private string? _contentError;
    [ObservableProperty] private string _statusText = Loc.T("Main.Ready");
    [ObservableProperty] private string _itemsText = "";
    [ObservableProperty] private double? _progress;
    [ObservableProperty] private string _filter = "All";
    [ObservableProperty] private string _sortBy = "Date";
    [ObservableProperty] private bool _sortDescending = true;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isSearchActive;
    [ObservableProperty] private System.Text.RegularExpressions.Regex? _searchHighlight;
    [ObservableProperty] private bool _searchAllMailboxes = true;
    [ObservableProperty] private string _listTitle = Loc.T("Main.InboxFolder");
    [ObservableProperty] private int _totalInView;
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private bool _workOffline;
    [ObservableProperty] private string _readingPanePosition;
    [ObservableProperty] private bool _showFolderPane;
    [ObservableProperty] private bool _showPreview;
    [ObservableProperty] private bool _hasAccounts;

    public bool HasSelection => SelectedItems.Count > 0;
    public bool IsDraftsFolder => SelectedNode?.Folder?.Special == SpecialFolder.Drafts;
    public bool IsSentFolder => SelectedNode?.Folder?.Special == SpecialFolder.Sent;

    public MainViewModel()
    {
        _readingPanePosition = Settings.ReadingPanePosition;
        _showFolderPane = Settings.ShowFolderPane;
        _showPreview = Settings.ShowPreview;
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, async (_, _) => await FlushRefreshAsync(), _ui);
        _refreshTimer.Stop();

        AppServices.Sync.StatusChanged += (_, e) => _ui.BeginInvoke(() =>
        {
            StatusText = e.Text;
            Progress = e.Progress;
            IsSyncing = AppServices.Sync.IsSyncing;
        });
        AppServices.Sync.FolderChanged += (_, e) => _ui.BeginInvoke(() =>
        {
            _pendingFolderRefresh.Add(e.FolderId);
            if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
        });
        AppServices.Sync.FoldersListChanged += (_, _) => _ui.BeginInvoke(() => LoadFolders());
        AppServices.Accounts.AccountsChanged += (_, _) => _ui.BeginInvoke(() => LoadFolders());
        Loc.LanguageChanged += (_, _) => _ui.BeginInvoke(OnLanguageChanged);
    }

    public void Initialize()
    {
        LoadFolders();
        AppServices.Sync.Start();
    }

    private void OnLanguageChanged()
    {
        foreach (var n in AllNodes()) n.RefreshDisplayName();
        if (IsSearchActive) { UpdateSearchText(); return; }
        ListTitle = SelectedNode?.Name ?? "";
        if (SelectedNode?.Folder == null) return;
        UpdateItemsText(SelectedNode);
        _ = ReloadMessagesAsync(true);
    }

    partial void OnReadingPanePositionChanged(string value) { Settings.ReadingPanePosition = value; Settings.Save(); }
    partial void OnShowFolderPaneChanged(bool value) { Settings.ShowFolderPane = value; Settings.Save(); }
    partial void OnShowPreviewChanged(bool value) { Settings.ShowPreview = value; Settings.Save(); }
    partial void OnWorkOfflineChanged(bool value)
    {
        AppServices.Sync.WorkOffline = value;
        StatusText = value ? Loc.T("Main.WorkingOffline") : Loc.T("Main.Connected");
    }

    public void LoadFolders()
    {
        var selectedId = SelectedNode?.Folder?.Id;
        var expanded = new HashSet<string>(AllNodes().Where(n => n.IsExpanded).Select(Key));
        var firstLoad = FolderTree.Count == 0;
        FolderTree.Clear();
        Favorites.Clear();

        var accounts = AppServices.Accounts.GetAccounts();
        HasAccounts = accounts.Any(a => a.Kind != AccountKind.Local);
        foreach (var a in accounts)
        {
            var folders = AppServices.Db.GetFolders(a.Id);
            var root = new FolderNode(a, null, FolderNode.AccountName(a)) { IsExpanded = true };
            var byName = new Dictionary<string, FolderNode>();
            var ordered = folders.OrderBy(f => f.ParentFullName == null ? FolderNode.SortKey(f) : 7).ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var f in ordered)
                byName[f.FullName] = new FolderNode(a, f, FolderNode.FriendlyName(f));
            foreach (var f in ordered)
            {
                var node = byName[f.FullName];
                if (f.ParentFullName != null && byName.TryGetValue(f.ParentFullName, out var parent)) parent.Children.Add(node);
                else root.Children.Add(node);
                if (expanded.Contains(Key(node))) node.IsExpanded = true;
            }
            if (!firstLoad && !expanded.Contains(Key(root))) root.IsExpanded = false;
            FolderTree.Add(root);

            if (a.Kind == AccountKind.Local) continue;
            var multi = accounts.Count(x => x.Kind != AccountKind.Local) > 1;
            foreach (var special in new[] { SpecialFolder.Inbox, SpecialFolder.Sent, SpecialFolder.Drafts, SpecialFolder.Trash })
            {
                if (multi && special != SpecialFolder.Inbox && Favorites.Any(x => x.Folder?.Special == special)) continue;
                var f = folders.FirstOrDefault(x => x.Special == special);
                if (f == null) continue;
                var suffix = multi && special == SpecialFolder.Inbox ? " - " + a.Email : "";
                var name = FolderNode.FriendlyName(f) + suffix;
                Favorites.Add(new FolderNode(a, f, name) { IsFavorite = true, NameSuffix = suffix });
            }
        }

        var target = (selectedId == null ? null : AllNodes().FirstOrDefault(n => n.Folder?.Id == selectedId && !n.IsFavorite))
                     ?? Favorites.FirstOrDefault()
                     ?? AllNodes().FirstOrDefault(n => n.Folder?.Special == SpecialFolder.Inbox);
        if (target != null && target.Folder?.Id != selectedId) SelectedNode = target;
        else if (target != null) { _suppressReload = true; SelectedNode = target; _suppressReload = false; }
        if (target != null) target.IsSelected = true;
    }

    private bool _suppressReload;
    private static string Key(FolderNode n) => n.Account.Id + "|" + (n.Folder?.FullName ?? "");

    public IEnumerable<FolderNode> AllNodes()
    {
        foreach (var r in FolderTree)
            foreach (var n in Walk(r)) yield return n;
        foreach (var f in Favorites) yield return f;
    }

    private static IEnumerable<FolderNode> Walk(FolderNode n)
    {
        yield return n;
        foreach (var c in n.Children)
            foreach (var x in Walk(c)) yield return x;
    }

    partial void OnSelectedNodeChanged(FolderNode? value)
    {
        OnPropertyChanged(nameof(IsDraftsFolder));
        OnPropertyChanged(nameof(IsSentFolder));
        if (_suppressReload || value?.Folder == null) return;
        if (IsSearchActive) { IsSearchActive = false; SearchText = ""; }
        ListTitle = value.Name;
        _ = ReloadMessagesAsync(false);
        var f = value.Folder;
        if (value.Account.Kind != AccountKind.Local && !WorkOffline && (f.Special == SpecialFolder.All || f.LastUid == 0 || f.Special == SpecialFolder.Inbox))
            _ = Task.Run(async () => { try { await AppServices.Sync.SyncFolderAsync(f.Id, CancellationToken.None); } catch { } });
    }

    partial void OnFilterChanged(string value) => _ = ReloadMessagesAsync(false);
    partial void OnSortByChanged(string value) => _ = ReloadMessagesAsync(false);
    partial void OnSortDescendingChanged(bool value) => _ = ReloadMessagesAsync(false);
}
