using System.Windows;
using System.Windows.Input;
using Imvello.Models;
using Imvello.Services;

namespace Imvello.Views;

public partial class MainWindow
{
    private static RoutedUICommand Cmd(string name, string text) => new(text, name, typeof(MainWindow));

    public static readonly RoutedUICommand NewMailCommand = Cmd(nameof(NewMailCommand), Loc.T("Main.NewEmail"));
    public static readonly RoutedUICommand ReplyCommand = Cmd(nameof(ReplyCommand), Loc.T("Main.Reply"));
    public static readonly RoutedUICommand ReplyAllCommand = Cmd(nameof(ReplyAllCommand), Loc.T("Main.ReplyAll"));
    public static readonly RoutedUICommand ForwardCommand = Cmd(nameof(ForwardCommand), Loc.T("Main.Forward"));
    public static readonly RoutedUICommand MarkReadCommand = Cmd(nameof(MarkReadCommand), Loc.T("Main.MarkAsRead"));
    public static readonly RoutedUICommand MarkUnreadCommand = Cmd(nameof(MarkUnreadCommand), Loc.T("Main.MarkAsUnread"));
    public static readonly RoutedUICommand ToggleReadCommand = Cmd(nameof(ToggleReadCommand), Loc.T("Main.UnreadRead"));
    public static readonly RoutedUICommand ToggleFlagCommand = Cmd(nameof(ToggleFlagCommand), Loc.T("Main.FollowUp"));
    public static readonly RoutedUICommand SearchCommand = Cmd(nameof(SearchCommand), Loc.T("Common.Search"));
    public static readonly RoutedUICommand SendReceiveCommand = Cmd(nameof(SendReceiveCommand), Loc.T("Main.SendReceiveAllFolders"));
    public static readonly RoutedUICommand CancelSyncCommand = Cmd(nameof(CancelSyncCommand), Loc.T("Main.CancelAll"));
    public static readonly RoutedUICommand UpdateFolderCommand = Cmd(nameof(UpdateFolderCommand), Loc.T("Main.UpdateFolder"));
    public static readonly RoutedUICommand DeleteCommand = Cmd(nameof(DeleteCommand), Loc.T("Main.Delete"));
    public static readonly RoutedUICommand ArchiveCommand = Cmd(nameof(ArchiveCommand), Loc.T("Main.ArchiveAction"));
    public static readonly RoutedUICommand MoveCommand = Cmd(nameof(MoveCommand), Loc.T("Main.Move"));
    public static readonly RoutedUICommand JunkCommand = Cmd(nameof(JunkCommand), Loc.T("Main.Junk"));
    public static readonly RoutedUICommand PrintCommand = Cmd(nameof(PrintCommand), Loc.T("Main.Print"));
    public static readonly RoutedUICommand SaveAsCommand = Cmd(nameof(SaveAsCommand), Loc.T("Main.SaveAs"));
    public static readonly RoutedUICommand BackstageCommand = Cmd(nameof(BackstageCommand), Loc.T("Main.FileMenu"));
    public static readonly RoutedUICommand MarkAllReadCommand = Cmd(nameof(MarkAllReadCommand), Loc.T("Main.MarkAllAsRead"));
    public static readonly RoutedUICommand NewFolderCommand = Cmd(nameof(NewFolderCommand), Loc.T("Main.NewFolder"));
    public static readonly RoutedUICommand RenameFolderCommand = Cmd(nameof(RenameFolderCommand), Loc.T("Main.RenameFolder"));
    public static readonly RoutedUICommand DeleteFolderCommand = Cmd(nameof(DeleteFolderCommand), Loc.T("Main.DeleteFolder"));
    public static readonly RoutedUICommand EmptyFolderCommand = Cmd(nameof(EmptyFolderCommand), Loc.T("Main.DeleteAll"));
    public static readonly RoutedUICommand ImportThunderbirdCommand = Cmd(nameof(ImportThunderbirdCommand), Loc.T("Main.ImportThunderbird"));
    public static readonly RoutedUICommand ImportMboxCommand = Cmd(nameof(ImportMboxCommand), Loc.T("Main.ImportMbox"));
    public static readonly RoutedUICommand ExportMboxCommand = Cmd(nameof(ExportMboxCommand), Loc.T("Main.ExportMbox"));
    public static readonly RoutedUICommand FolderPropertiesCommand = Cmd(nameof(FolderPropertiesCommand), Loc.T("Main.FolderProperties"));
    public static readonly RoutedUICommand TogglePreviewCommand = Cmd(nameof(TogglePreviewCommand), Loc.T("Main.MessagePreview"));
    public static readonly RoutedUICommand SortCommand = Cmd(nameof(SortCommand), Loc.T("Main.ArrangeBy"));
    public static readonly RoutedUICommand ReverseSortCommand = Cmd(nameof(ReverseSortCommand), Loc.T("Main.ReverseSort"));
    public static readonly RoutedUICommand ToggleFolderPaneCommand = Cmd(nameof(ToggleFolderPaneCommand), Loc.T("Main.FolderPane"));
    public static readonly RoutedUICommand AboutCommand = Cmd(nameof(AboutCommand), Loc.T("Main.About"));
    public static readonly RoutedUICommand OpenDataFolderCommand = Cmd(nameof(OpenDataFolderCommand), Loc.T("Main.OpenDataFolder"));
    public static readonly RoutedUICommand OptionsCommand = Cmd(nameof(OptionsCommand), Loc.T("Main.Options"));

    private void Bind(ICommand cmd, Action<object?> exec, Func<bool>? can = null)
    {
        var binding = new CommandBinding(cmd, (_, e) => { e.Handled = true; exec(e.Parameter); });
        if (can != null) binding.CanExecute += (_, e) => { e.CanExecute = can(); e.Handled = true; };
        CommandBindings.Add(binding);
    }

    private ViewModels.MessageItem? Primary => Vm.CurrentItem ?? Vm.SelectedItems.FirstOrDefault();
    private bool HasFolder => Vm.SelectedNode?.Folder != null;

    private void RegisterCommands()
    {
        Func<bool> sel = () => Vm.HasSelection;
        Func<bool> folder = () => HasFolder;
        Bind(NewMailCommand, _ => ComposeWindow.ShowCompose(ComposeMode.New, null));
        Bind(ReplyCommand, _ => { if (Primary is { } m) ComposeWindow.ShowCompose(ComposeMode.Reply, m.Id); }, sel);
        Bind(ReplyAllCommand, _ => { if (Primary is { } m) ComposeWindow.ShowCompose(ComposeMode.ReplyAll, m.Id); }, sel);
        Bind(ForwardCommand, _ => { if (Primary is { } m) ComposeWindow.ShowCompose(ComposeMode.Forward, m.Id); }, sel);
        Bind(MarkReadCommand, _ => _ = Vm.SetReadSelectedAsync(true), sel);
        Bind(MarkUnreadCommand, _ => _ = Vm.SetReadSelectedAsync(false), sel);
        Bind(ToggleReadCommand, _ => { if (Vm.SelectedItems.FirstOrDefault() is { } m) _ = Vm.SetReadSelectedAsync(!m.IsRead); }, sel);
        Bind(ToggleFlagCommand, _ => _ = Vm.ToggleFlagAsync(Vm.SelectedItems.ToList()), sel);
        Bind(SearchCommand, _ => { SearchBox.Focus(); SearchBox.SelectAll(); });
        Bind(SendReceiveCommand, _ => _ = Vm.SendReceiveAsync());
        Bind(CancelSyncCommand, _ => { if (Vm.IsSyncing) _ = Vm.SendReceiveAsync(); }, () => Vm.IsSyncing);
        Bind(UpdateFolderCommand, _ => _ = Vm.UpdateFolderAsync(), () => HasFolder && Vm.SelectedNode!.Account.Kind != AccountKind.Local);
        Bind(DeleteCommand, _ => DeleteSelected(), sel);
        Bind(ArchiveCommand, _ => _ = Vm.ArchiveSelectedAsync(), sel);
        Bind(JunkCommand, _ => _ = Vm.JunkSelectedAsync(), sel);
        Bind(MoveCommand, _ => MoveSelected(), sel);
        Bind(PrintCommand, _ => _ = ReadingPaneView.PrintAsync(), () => Vm.CurrentContent != null && ReadingPaneView.IsVisible);
        Bind(SaveAsCommand, _ => SaveSelectedAs(), sel);
        Bind(BackstageCommand, _ => BackstageView.Open());
        Bind(MarkAllReadCommand, _ => { if (Vm.SelectedNode is { } n) MarkAllRead(n); }, folder);
        Bind(NewFolderCommand, _ => NewFolder(Vm.SelectedNode), () => Vm.SelectedNode != null);
        Bind(RenameFolderCommand, _ => { if (Vm.SelectedNode is { } n) RenameFolder(n); }, () => HasFolder && Vm.SelectedNode!.Folder!.Special == SpecialFolder.None);
        Bind(DeleteFolderCommand, _ => { if (Vm.SelectedNode is { } n) DeleteFolder(n); }, () => HasFolder && Vm.SelectedNode!.Folder!.Special == SpecialFolder.None);
        Bind(EmptyFolderCommand, _ => { if (Vm.SelectedNode is { } n) EmptyFolder(n); }, folder);
        Bind(ImportThunderbirdCommand, _ => ImportThunderbird());
        Bind(ImportMboxCommand, _ => ImportMbox(null));
        Bind(ExportMboxCommand, _ => ExportMbox(null), folder);
        Bind(FolderPropertiesCommand, _ => { if (Vm.SelectedNode is { } n) ShowFolderProperties(n); }, folder);
        Bind(TogglePreviewCommand, _ => Vm.ShowPreview = !Vm.ShowPreview);
        Bind(SortCommand, p => { if (p is string s && s.Length > 0) Vm.SortBy = s; });
        Bind(ReverseSortCommand, _ => Vm.SortDescending = !Vm.SortDescending);
        Bind(ToggleFolderPaneCommand, _ => Vm.ShowFolderPane = !Vm.ShowFolderPane);
        Bind(AboutCommand, _ => AboutDialog.Show(this));
        Bind(OpenDataFolderCommand, _ => OpenDataFolder());
        Bind(OptionsCommand, _ => OpenOptions());
    }

    private void DeleteSelected()
    {
        if (Vm.Settings.ConfirmDelete && !Confirm(Loc.F("Main.DeleteSelectedConfirm", Vm.SelectedItems.Count))) return;
        _ = Vm.DeleteSelectedAsync();
    }

    private void MoveSelected()
    {
        var target = FolderPickerDialog.Show(this, Loc.T("Main.MoveItemsTitle"), Vm.FolderTree, Vm.SelectedNode?.Folder?.Id);
        if (target != null) _ = Vm.MoveSelectedAsync(target.Id);
    }

    private bool Confirm(string text, string title = "Imvello") =>
        MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void Info(string text) => MessageBox.Show(this, text, "Imvello", MessageBoxButton.OK, MessageBoxImage.Information);

    private async Task RunAsync(string? busy, Func<Task> op, string? done = null)
    {
        if (busy != null) Vm.StatusText = busy;
        try
        {
            await Task.Run(op);
            if (done != null) Vm.StatusText = done;
        }
        catch (Exception ex)
        {
            Vm.StatusText = Loc.F("Common.ErrorPrefix", ex.Message);
            MessageBox.Show(this, ex.Message, "Imvello", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
