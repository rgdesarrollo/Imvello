using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Imvello.Models;
using Imvello.Services;
using Imvello.ViewModels;

namespace Imvello.Views;

public partial class FolderPane : UserControl
{
    public const string DragFormat = "Imvello.Messages";

    public FolderPane() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;
    private MainWindow? Main => Window.GetWindow(this) as MainWindow;

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Vm == null || e.NewValue is not FolderNode node) return;
        var other = sender == FavoritesTree ? Vm.FolderTree.SelectMany(Flatten) : Vm.Favorites;
        foreach (var n in other.Where(n => n.IsSelected && n != node)) n.IsSelected = false;
        if (node.Folder == null || !node.Folder.Selectable) return;
        if (Vm.SelectedNode?.Folder?.Id != node.Folder.Id || Vm.SelectedNode.IsFavorite != node.IsFavorite) Vm.SelectedNode = node;
    }

    private static IEnumerable<FolderNode> Flatten(FolderNode n) => new[] { n }.Concat(n.Children.SelectMany(Flatten));

    private static FolderNode? NodeOf(object sender) => (sender as FrameworkElement)?.DataContext as FolderNode;

    private void Item_DragOver(object sender, DragEventArgs e)
    {
        var node = NodeOf(sender);
        e.Effects = node?.Folder is { Selectable: true } && e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Item_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (NodeOf(sender)?.Folder is not { Selectable: true } f || Vm == null || !e.Data.GetDataPresent(DragFormat)) return;
        await Vm.MoveSelectedAsync(f.Id);
    }

    private void Item_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem tvi || NodeOf(sender) is not { } node || Main is not { } main) return;
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(tvi, d) != null) return;
        e.Handled = true;
        var menu = new ContextMenu();
        void Add(string header, string icon, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled, Icon = new TextBlock { Text = icon, Style = (Style)FindResource("Icon"), FontSize = 13 } };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        var isLocal = node.Account.Kind == AccountKind.Local;
        if (node.IsAccountNode)
        {
            Add(Loc.T("Main.NewFolderEllipsis"), "\uE8F4", () => main.NewFolder(node));
            if (!isLocal)
            {
                Add(Loc.T("Main.UpdateAccount"), "\uE72C", () => main.SyncAccount(node.Account.Id));
                menu.Items.Add(new Separator());
                if (AccountService.CanReconnect(node.Account))
                    Add(Loc.F("Main.ReconnectWith", node.Account.Kind == AccountKind.Outlook ? "Microsoft" : "Google"), "\uE8D7", () => main.ReconnectAccount(node.Account));
                Add(Loc.T("Main.AccountSettingsEllipsis"), "\uE713", () => main.OpenAccountSettings(node.Account));
                Add(Loc.T("Main.RemoveAccount"), "\uE74D", () => main.RemoveAccount(node.Account));
            }
            else
            {
                Add(Loc.T("Main.ImportThunderbirdEllipsis"), "\uE896", () => main.ImportThunderbird());
            }
        }
        else
        {
            var f = node.Folder!;
            var special = f.Special != SpecialFolder.None || (isLocal && f.ParentFullName == null && f.DisplayName is "Inbox" or "Imported");
            if (Vm != null && Vm.SelectedNode != node) { node.IsSelected = true; }
            Add(Loc.T("Main.Open"), "\uE8E5", () => node.IsSelected = true, f.Selectable);
            if (!isLocal) Add(Loc.T("Main.UpdateFolder"), "\uE72C", () => main.SyncFolder(f.Id), f.Selectable);
            menu.Items.Add(new Separator());
            Add(Loc.T("Main.NewFolderEllipsis"), "\uE8F4", () => main.NewFolder(node));
            Add(Loc.T("Main.RenameFolderEllipsis"), "\uE8AC", () => main.RenameFolder(node), !special);
            Add(Loc.T("Main.DeleteFolder"), "\uE74D", () => main.DeleteFolder(node), !special);
            menu.Items.Add(new Separator());
            Add(Loc.T("Main.MarkAllAsRead"), "\uE8C3", () => main.MarkAllRead(node), f.Selectable);
            Add(f.Special is SpecialFolder.Trash or SpecialFolder.Junk ? Loc.T("Main.EmptyFolder") : Loc.T("Main.DeleteAll"), "\uE894", () => main.EmptyFolder(node), f.Selectable);
            menu.Items.Add(new Separator());
            Add(Loc.T("Main.ImportMboxIntoFolder"), "\uE8E5", () => main.ImportMbox(node), f.Selectable);
            Add(Loc.T("Main.ExportFolderToMboxEllipsis"), "\uE898", () => main.ExportMbox(node), f.Selectable);
            Add(Loc.T("Main.PropertiesEllipsis"), "\uE946", () => main.ShowFolderProperties(node));
        }
        menu.PlacementTarget = tvi;
        menu.IsOpen = true;
    }
}
