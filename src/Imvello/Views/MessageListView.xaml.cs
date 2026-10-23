using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using Imvello.Services;
using Imvello.ViewModels;

namespace Imvello.Views;

public partial class MessageListView : UserControl
{
    private Point _dragStart;
    private bool _dragArmed;
    private MainViewModel? _vm;
    private ICollectionView? _view;
    private bool _restoring;

    public MessageListView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
        Loc.LanguageChanged += (_, _) => UpdateSortText();
    }

    private MainWindow? Main => Window.GetWindow(this) as MainWindow;

    private void Attach(MainViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= Vm_PropertyChanged;
            _vm.RestoreSelection -= Vm_RestoreSelection;
            _vm.Messages.CollectionChanged -= Messages_CollectionChanged;
        }
        _vm = vm;
        if (vm == null) { List.ItemsSource = null; return; }
        vm.PropertyChanged += Vm_PropertyChanged;
        vm.RestoreSelection += Vm_RestoreSelection;
        vm.Messages.CollectionChanged += Messages_CollectionChanged;
        _view = CollectionViewSource.GetDefaultView(vm.Messages);
        List.ItemsSource = _view;
        UpdateGrouping();
        UpdateSortText();
        UpdateEmpty();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SortBy):
            case nameof(MainViewModel.SortDescending):
                UpdateSortText();
                UpdateGrouping();
                break;
            case nameof(MainViewModel.IsSearchActive):
                UpdateGrouping();
                break;
            case nameof(MainViewModel.Filter):
                foreach (var rb in ((Panel)AllTab.Parent).Children.OfType<RadioButton>())
                    if ((string)rb.Tag == _vm.Filter && rb.IsChecked != true) rb.IsChecked = true;
                break;
        }
    }

    private void UpdateGrouping()
    {
        if (_view == null || _vm == null) return;
        var group = _vm.SortBy == "Date" && !_vm.IsSearchActive;
        var has = _view.GroupDescriptions.Count > 0;
        if (group == has) return;
        if (group) _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MessageItem.GroupName)));
        else _view.GroupDescriptions.Clear();
    }

    private void UpdateSortText()
    {
        if (_vm == null) return;
        SortText.Text = Loc.F("Main.BySort", SortLabel(_vm.SortBy));
        SortArrow.Text = _vm.SortDescending ? "\uE74B" : "\uE74A";
    }

    private static string SortLabel(string s) => s switch
    {
        "Date" => Loc.T("Main.SortByDate"),
        "From" => Loc.T("Main.SortByFrom"),
        "Subject" => Loc.T("Main.SortBySubject"),
        "Size" => Loc.T("Main.SortBySize"),
        _ => s
    };

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmpty();

    private void UpdateEmpty() => EmptyText.Visibility = _vm != null && _vm.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Vm_RestoreSelection(List<MessageItem> items, MessageItem? current)
    {
        _restoring = true;
        try
        {
            List.SelectedItems.Clear();
            foreach (var i in items) List.SelectedItems.Add(i);
        }
        finally { _restoring = false; }
        var focus = current ?? items.FirstOrDefault();
        if (focus != null) List.ScrollIntoView(focus);
        _vm?.OnSelectionChanged(List.SelectedItems.Cast<MessageItem>().ToList());
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoring || _vm == null) return;
        _vm.OnSelectionChanged(List.SelectedItems.Cast<MessageItem>().ToList());
    }

    private static MessageItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as MessageItem;

    private void Item_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is { } item) { e.Handled = true; Main?.OpenMessage(item); }
    }

    private void Item_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragArmed = !(e.OriginalSource is DependencyObject d && FindParent<ButtonBase>(d) != null);
        if (sender is ListBoxItem { IsSelected: true } && List.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None && _dragArmed)
            e.Handled = true;
    }

    private void Item_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed || sender is not ListBoxItem lbi) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragArmed = false;
        if (!lbi.IsSelected) { List.SelectedItems.Clear(); lbi.IsSelected = true; }
        var ids = List.SelectedItems.Cast<MessageItem>().Select(m => m.Id).ToArray();
        if (ids.Length == 0) return;
        DragDrop.DoDragDrop(lbi, new DataObject(FolderPane.DragFormat, ids), DragDropEffects.Move);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_dragArmed && e.OriginalSource is DependencyObject d && FindParent<ListBoxItem>(d) is { } lbi && List.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            List.SelectedItems.Clear();
            lbi.IsSelected = true;
            lbi.Focus();
        }
        _dragArmed = false;
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private async void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item || _vm == null) return;
        e.Handled = true;
        if (!List.SelectedItems.Contains(item)) { List.SelectedItems.Clear(); List.SelectedItems.Add(item); }
        await _vm.DeleteSelectedAsync();
    }

    private async void FlagItem_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item || _vm == null) return;
        e.Handled = true;
        await _vm.ToggleFlagAsync([item]);
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        var m = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        MenuItem Item(string header, bool check, Action a)
        {
            var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = check };
            mi.Click += (_, _) => a();
            return mi;
        }
        foreach (var s in new[] { "Date", "From", "Subject", "Size" })
            m.Items.Add(Item(SortLabel(s), vm.SortBy == s, () => vm.SortBy = s));
        m.Items.Add(new Separator());
        m.Items.Add(Item(Loc.T("Main.NewestOnTop"), vm.SortDescending, () => vm.SortDescending = true));
        m.Items.Add(Item(Loc.T("Main.OldestOnTop"), !vm.SortDescending, () => vm.SortDescending = false));
        m.IsOpen = true;
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (_vm != null && sender is RadioButton { Tag: string f } && _vm.Filter != f) _vm.Filter = f;
    }

    private async void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await _vm.DeleteSelectedAsync();
        }
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            e.Handled = true;
            Main?.PermanentDeleteSelected();
        }
        else if (e.Key == Key.Enter && List.SelectedItem is MessageItem item)
        {
            e.Handled = true;
            Main?.OpenMessage(item);
        }
        else if (e.Key == Key.Insert)
        {
            e.Handled = true;
            await _vm.ToggleFlagAsync(_vm.SelectedItems);
        }
    }

    private async void List_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_vm == null || e.ExtentHeight <= 0) return;
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - Math.Max(400, e.ViewportHeight))
            await _vm.LoadMoreAsync();
    }

    private void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_vm == null || Main == null || _vm.SelectedItems.Count == 0) { e.Handled = true; return; }
        var menu = List.ContextMenu!;
        menu.Items.Clear();
        var single = _vm.SelectedItems.Count == 1;
        void Add(string header, string icon, RoutedCommand cmd, string? gesture = null, object? param = null)
        {
            menu.Items.Add(new MenuItem
            {
                Header = header,
                Command = cmd,
                CommandParameter = param,
                CommandTarget = List,
                InputGestureText = gesture ?? "",
                Icon = new TextBlock { Text = icon, Style = (Style)FindResource("Icon"), FontSize = 13 }
            });
        }
        if (single)
        {
            Add(Loc.T("Main.Reply"), "\uE97A", MainWindow.ReplyCommand, "Ctrl+R");
            Add(Loc.T("Main.ReplyAll"), "\uEE0A", MainWindow.ReplyAllCommand, "Ctrl+Shift+R");
            Add(Loc.T("Main.Forward"), "\uE72A", MainWindow.ForwardCommand, "Ctrl+F");
            menu.Items.Add(new Separator());
        }
        var anyUnread = _vm.SelectedItems.Any(i => !i.IsRead);
        if (anyUnread) Add(Loc.T("Main.MarkAsRead"), "\uE8C3", MainWindow.MarkReadCommand, "Ctrl+Q");
        else Add(Loc.T("Main.MarkAsUnread"), "\uE715", MainWindow.MarkUnreadCommand, "Ctrl+U");
        Add(_vm.SelectedItems.All(i => i.IsFlagged) ? Loc.T("Main.ClearFlag") : Loc.T("Main.Flag"), "\uE7C1", MainWindow.ToggleFlagCommand, "Insert");
        menu.Items.Add(new Separator());
        Add(Loc.T("Main.MoveToFolderEllipsis"), "\uE8DE", MainWindow.MoveCommand, "Ctrl+Shift+V");
        Add(Loc.T("Main.ArchiveAction"), "\uE7B8", MainWindow.ArchiveCommand, "Backspace");
        Add(Loc.T("Main.Junk"), "\uE730", MainWindow.JunkCommand);
        Add(Loc.T("Main.Delete"), "\uE74D", MainWindow.DeleteCommand, "Del");
        menu.Items.Add(new Separator());
        if (single)
        {
            Add(Loc.T("Main.SaveAsEllipsis"), "\uE74E", MainWindow.SaveAsCommand);
            Add(Loc.T("Main.Print"), "\uE749", MainWindow.PrintCommand, "Ctrl+P");
        }
    }
}
