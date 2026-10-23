using Imvello.Models;
using Imvello.Services;

namespace Imvello.ViewModels;

public partial class MainViewModel
{
    public async Task ReloadMessagesAsync(bool keepSelection)
    {
        var node = SelectedNode;
        if (IsSearchActive) { await RunSearchAsync(); return; }
        if (node?.Folder == null) { Messages.Clear(); return; }
        var version = ++_listVersion;
        var folderId = node.Folder.Id;
        var keep = keepSelection ? SelectedItems.Select(s => s.Id).ToHashSet() : new HashSet<long>();
        var currentId = keepSelection ? CurrentItem?.Id : null;
        var count = keepSelection ? Math.Max(PageSize, Messages.Count) : PageSize;
        bool unread = Filter == "Unread", flagged = Filter == "Flagged";

        var (rows, total) = await Task.Run(async () =>
        {
            var r = await AppServices.Db.GetHeadersPageAsync(folderId, 0, count, SortBy, SortDescending, unread, flagged);
            var t = await AppServices.Db.CountAsync(folderId, unread, flagged);
            return (r, t);
        });
        if (version != _listVersion) return;

        var showTo = node.Folder.Special is SpecialFolder.Sent or SpecialFolder.Drafts or SpecialFolder.Outbox;
        if (keepSelection && Messages.Count == rows.Count && Messages.Select(m => m.Id).SequenceEqual(rows.Select(r => r.Id)))
        {
            for (int i = 0; i < rows.Count; i++)
            {
                Messages[i].IsRead = rows[i].IsRead;
                Messages[i].IsFlagged = rows[i].IsFlagged;
            }
        }
        else if (keepSelection && Messages.Count > 0)
        {
            MergeMessages(rows, showTo);
            RestoreSelection?.Invoke(Messages.Where(m => keep.Contains(m.Id)).ToList(), Messages.FirstOrDefault(m => m.Id == currentId));
        }
        else
        {
            Messages.Clear();
            foreach (var r in rows) Messages.Add(new MessageItem(r, showTo));
            if (!keepSelection) ClearContent();
            else RestoreSelection?.Invoke(Messages.Where(m => keep.Contains(m.Id)).ToList(), Messages.FirstOrDefault(m => m.Id == currentId));
        }
        TotalInView = total;
        UpdateItemsText(node);
    }

    public event Action<List<MessageItem>, MessageItem?>? RestoreSelection;

    private void MergeMessages(List<MessageHeader> rows, bool showTo)
    {
        var wanted = rows.Select(r => r.Id).ToHashSet();
        for (int i = Messages.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Messages[i].Id)) Messages.RemoveAt(i);
        var existing = Messages.ToDictionary(m => m.Id);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (!existing.TryGetValue(r.Id, out var item))
            {
                Messages.Insert(i, new MessageItem(r, showTo));
                continue;
            }
            item.IsRead = r.IsRead;
            item.IsFlagged = r.IsFlagged;
            if (!ReferenceEquals(Messages[i], item)) Messages.Move(Messages.IndexOf(item), i);
        }
    }

    private void UpdateItemsText(FolderNode? node)
    {
        var f = node?.Folder;
        if (f == null) { ItemsText = ""; return; }
        var folder = AppServices.Db.GetFolder(f.Id);
        if (folder == null) return;
        node!.UnreadCount = folder.UnreadCount;
        node.TotalCount = folder.TotalCount;
        foreach (var n in AllNodes().Where(n => n.Folder?.Id == f.Id)) { n.UnreadCount = folder.UnreadCount; n.TotalCount = folder.TotalCount; }
        ItemsText = Loc.F("Main.ItemsCount", folder.TotalCount) + (folder.UnreadCount > 0 ? "    " + Loc.F("Main.UnreadCount", folder.UnreadCount) : "");
    }

    public async Task LoadMoreAsync()
    {
        if (_loadingMore || IsSearchActive || SelectedNode?.Folder == null || Messages.Count >= TotalInView) return;
        _loadingMore = true;
        try
        {
            var version = _listVersion;
            var folderId = SelectedNode.Folder.Id;
            bool unread = Filter == "Unread", flagged = Filter == "Flagged";
            var offset = Messages.Count;
            var rows = await Task.Run(() => AppServices.Db.GetHeadersPageAsync(folderId, offset, PageSize, SortBy, SortDescending, unread, flagged));
            if (version != _listVersion) return;
            var showTo = SelectedNode.Folder.Special is SpecialFolder.Sent or SpecialFolder.Drafts;
            var existing = Messages.Select(m => m.Id).ToHashSet();
            foreach (var r in rows.Where(r => !existing.Contains(r.Id))) Messages.Add(new MessageItem(r, showTo));
        }
        finally { _loadingMore = false; }
    }

    private async Task FlushRefreshAsync()
    {
        _refreshTimer.Stop();
        var ids = _pendingFolderRefresh.ToList();
        _pendingFolderRefresh.Clear();
        foreach (var id in ids)
        {
            var f = AppServices.Db.GetFolder(id);
            if (f == null) continue;
            foreach (var n in AllNodes().Where(n => n.Folder?.Id == id))
            {
                n.UnreadCount = f.UnreadCount;
                n.TotalCount = f.TotalCount;
                n.Folder = f;
            }
        }
        if (SelectedNode?.Folder != null && ids.Contains(SelectedNode.Folder.Id) && !IsSearchActive)
            await ReloadMessagesAsync(true);
    }

    public void OnSelectionChanged(List<MessageItem> selected)
    {
        SelectedItems = selected;
        OnPropertyChanged(nameof(HasSelection));
        var item = selected.Count == 1 ? selected[0] : null;
        if (item?.Id == CurrentItem?.Id && item != null) return;
        CurrentItem = item;
        if (item == null) { ClearContent(); return; }
        _ = LoadContentAsync(item);
    }

    private void ClearContent()
    {
        _contentCts?.Cancel();
        CurrentContent = null;
        ContentError = null;
        IsLoadingContent = false;
    }

    private async Task LoadContentAsync(MessageItem item)
    {
        _contentCts?.Cancel();
        var cts = _contentCts = new CancellationTokenSource();
        ContentError = null;
        if (AppServices.Messages.TryGetCachedContent(item.Id) is { } cached)
        {
            IsLoadingContent = false;
            CurrentContent = cached;
            OnContentShown(item);
            return;
        }
        IsLoadingContent = true;
        CurrentContent = null;
        try
        {
            if (!AppServices.Messages.IsBodyLocal(item.Header)) await Task.Delay(40, cts.Token);
            var content = await Task.Run(() => AppServices.Messages.GetContentAsync(item.Id, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || CurrentItem?.Id != item.Id) return;
            CurrentContent = content;
            OnContentShown(item);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) ContentError = Loc.F("Main.MessageDisplayError", ex.Message);
        }
        finally
        {
            if (_contentCts == cts) IsLoadingContent = false;
        }
    }

    private void OnContentShown(MessageItem item)
    {
        if (Settings.MarkAsReadWhenViewed && !item.IsRead)
        {
            item.IsRead = true;
            _ = RunActionAsync(() => AppServices.Messages.SetReadAsync([item.Id], true), refresh: false);
        }
        var i = Messages.IndexOf(item);
        if (i < 0) return;
        var around = new[] { i + 1, i - 1, i + 2 }.Where(n => n >= 0 && n < Messages.Count).Select(n => Messages[n].Header).ToList();
        _ = Task.Run(() => AppServices.Messages.PrefetchContentAsync(around, 1));
    }

    public async Task RunSearchAsync()
    {
        var q = SearchText.Trim();
        if (q.Length == 0) { await ClearSearchAsync(); return; }
        IsSearchActive = true;
        var version = ++_listVersion;
        long? folderId = SearchAllMailboxes ? null : SelectedNode?.Folder?.Id;
        var rows = await Task.Run(() => AppServices.Db.SearchAsync(q, null, folderId, 2000));
        if (version != _listVersion) return;
        SearchHighlight = Imvello.Data.MailDatabase.BuildHighlightRegex(q);
        Messages.Clear();
        foreach (var r in rows) Messages.Add(new MessageItem(r));
        ClearContent();
        TotalInView = rows.Count;
        UpdateSearchText();
    }

    private void UpdateSearchText()
    {
        ListTitle = Loc.T("Main.SearchResults");
        ItemsText = Loc.F("Main.SearchResultCount", TotalInView) + (TotalInView >= 2000 ? Loc.T("Main.SearchResultLimit") : "");
    }

    public async Task ClearSearchAsync()
    {
        SearchText = "";
        if (!IsSearchActive) return;
        IsSearchActive = false;
        SearchHighlight = null;
        ListTitle = SelectedNode?.Name ?? "";
        await ReloadMessagesAsync(false);
    }

    public async Task RunActionAsync(Func<Task> action, bool refresh = true)
    {
        try
        {
            await Task.Run(action);
        }
        catch (Exception ex)
        {
            StatusText = Loc.F("Common.ErrorPrefix", ex.Message);
            System.Windows.MessageBox.Show(ex.Message, "Imvello", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        if (refresh) await ReloadMessagesAsync(true);
        else UpdateItemsText(SelectedNode);
    }

    private List<long> TargetIds() => SelectedItems.Select(s => s.Id).ToList();

    public Task DeleteSelectedAsync() => RemoveAndRunAsync(ids => AppServices.Messages.DeleteAsync(ids));
    public Task ArchiveSelectedAsync() => RemoveAndRunAsync(ids => AppServices.Messages.ArchiveAsync(ids));
    public Task JunkSelectedAsync() => RemoveAndRunAsync(ids => AppServices.Messages.MarkJunkAsync(ids));
    public Task MoveSelectedAsync(long folderId) => RemoveAndRunAsync(ids => AppServices.Messages.MoveAsync(ids, folderId));

    public Task PermanentDeleteSelectedAsync()
    {
        var headers = SelectedItems.Select(i => i.Header).ToList();
        return RemoveAndRunAsync(async _ =>
        {
            foreach (var g in headers.GroupBy(h => h.FolderId))
                if (AppServices.Db.GetFolder(g.Key) is { } f)
                    await AppServices.Messages.PermanentDeleteAsync(f, g.ToList());
        });
    }

    private async Task RemoveAndRunAsync(Func<List<long>, Task> op)
    {
        var ids = TargetIds();
        if (ids.Count == 0) return;
        var items = SelectedItems.ToList();
        var nextIndex = Messages.IndexOf(items.Last()) + 1;
        var next = nextIndex < Messages.Count ? Messages[nextIndex] : Messages.Count > items.Count ? Messages.FirstOrDefault(m => !items.Contains(m)) : null;
        foreach (var i in items) Messages.Remove(i);
        ClearContent();
        CurrentItem = null;
        if (next != null) RestoreSelection?.Invoke([next], next);
        await RunActionAsync(() => op(ids));
    }

    public async Task SetReadSelectedAsync(bool read)
    {
        var items = SelectedItems.ToList();
        foreach (var i in items) i.IsRead = read;
        await RunActionAsync(() => AppServices.Messages.SetReadAsync(items.Select(i => i.Id), read), refresh: false);
    }

    public async Task ToggleFlagAsync(IEnumerable<MessageItem> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        var flag = !list[0].IsFlagged;
        foreach (var i in list) i.IsFlagged = flag;
        await RunActionAsync(() => AppServices.Messages.SetFlaggedAsync(list.Select(i => i.Id), flag), refresh: Filter == "Flagged");
    }

    public async Task SendReceiveAsync()
    {
        if (IsSyncing) { _syncCts?.Cancel(); return; }
        _syncCts = new CancellationTokenSource();
        IsSyncing = true;
        StatusText = Loc.T("Main.SendReceiveProgress");
        try { await Task.Run(() => AppServices.Sync.SyncAllAsync(_syncCts.Token)); }
        finally { IsSyncing = false; Progress = null; }
    }

    public async Task UpdateFolderAsync()
    {
        var f = SelectedNode?.Folder;
        if (f == null || SelectedNode!.Account.Kind == AccountKind.Local) return;
        StatusText = Loc.F("Main.UpdatingNamedFolder", SelectedNode.Name);
        try
        {
            await Task.Run(() => AppServices.Sync.SyncFolderAsync(f.Id, CancellationToken.None));
            StatusText = Loc.F("Main.NameUpToDate", SelectedNode?.Name ?? "");
        }
        catch (Exception ex) { StatusText = Loc.F("Common.ErrorPrefix", ex.Message); }
    }
}
