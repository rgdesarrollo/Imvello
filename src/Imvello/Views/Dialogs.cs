using System.Windows;
using System.Windows.Controls;
using Imvello.Models;
using Imvello.Services;
using Imvello.ViewModels;

namespace Imvello.Views;

public static class DialogHelper
{
    public static Window Create(Window? owner, string title, double width)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            Style = (Style)Application.Current.FindResource("ImvelloWindow")
        };
        if (owner is { IsLoaded: true }) w.Owner = owner;
        return w;
    }

    public static StackPanel Buttons(Window w, string okText, Func<bool>? onOk = null)
    {
        var ok = new Button { Content = okText, Style = (Style)Application.Current.FindResource("PrimaryButton"), IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = Loc.T("Common.Cancel"), Style = (Style)Application.Current.FindResource("SecondaryButton"), IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => { if (onOk == null || onOk()) w.DialogResult = true; };
        return new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } };
    }
}

public static class InputDialog
{
    public static string? Show(Window? owner, string title, string prompt, string initial = "")
    {
        var w = DialogHelper.Create(owner, title, 380);
        var box = new TextBox { Text = initial, Style = (Style)Application.Current.FindResource("FieldBox"), Height = 28, Margin = new Thickness(0, 6, 0, 0) };
        w.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children = { new TextBlock { Text = prompt }, box, DialogHelper.Buttons(w, Loc.T("Common.OK"), () => box.Text.Trim().Length > 0) }
        };
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? box.Text.Trim() : null;
    }
}

public static class FolderPickerDialog
{
    public static MailFolder? Show(Window? owner, string title, IEnumerable<FolderNode> roots, long? excludeFolderId = null)
    {
        var w = DialogHelper.Create(owner, title, 380);
        var tree = new TreeView { Height = 340, Margin = new Thickness(0, 6, 0, 0) };
        TreeViewItem Build(FolderNode n)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = n.Icon, Style = (Style)Application.Current.FindResource("Icon"), FontSize = 13, Margin = new Thickness(0, 0, 6, 0) });
            header.Children.Add(new TextBlock { Text = n.Name });
            var item = new TreeViewItem { Header = header, Tag = n, IsExpanded = true, Padding = new Thickness(2) };
            foreach (var c in n.Children) item.Items.Add(Build(c));
            return item;
        }
        foreach (var r in roots) tree.Items.Add(Build(r));
        MailFolder? result = null;
        bool Accept()
        {
            if ((tree.SelectedItem as TreeViewItem)?.Tag is not FolderNode { Folder: { Selectable: true } f } || f.Id == excludeFolderId) return false;
            result = f;
            return true;
        }
        tree.MouseDoubleClick += (_, e) => { if (Accept()) { w.DialogResult = true; e.Handled = true; } };
        w.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children = { new TextBlock { Text = Loc.T("Dialogs.ChooseFolder") }, tree, DialogHelper.Buttons(w, Loc.T("Common.OK"), Accept) }
        };
        return w.ShowDialog() == true ? result : null;
    }
}

public static class FolderPropertiesDialog
{
    public static void Show(Window? owner, FolderNode node)
    {
        var f = node.Folder;
        var w = DialogHelper.Create(owner, Loc.F("Dialogs.FolderPropertiesTitle", node.Name), 420);
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Row(string k, string v)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var r = grid.RowDefinitions.Count - 1;
            var a = new TextBlock { Text = k, Margin = new Thickness(0, 3, 0, 3) };
            a.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
            var b = new TextBox { Text = v, IsReadOnly = true, BorderThickness = new Thickness(0), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
            Grid.SetRow(a, r); Grid.SetRow(b, r); Grid.SetColumn(b, 1);
            grid.Children.Add(a); grid.Children.Add(b);
        }
        Row(Loc.T("Dialogs.AccountLabel"), node.Account.Kind == AccountKind.Local ? node.Account.DisplayName : node.Account.Email);
        if (f != null)
        {
            var counts = AppServices.Db.GetFolder(f.Id) ?? f;
            Row(Loc.T("Dialogs.NameLabel"), f.DisplayName);
            Row(Loc.T("Dialogs.FullPath"), f.FullName);
            Row(Loc.T("Dialogs.TypeLabel"), f.Special == SpecialFolder.None ? Loc.T("Dialogs.MailFolder") : f.Special.ToString());
            Row(Loc.T("Dialogs.TotalItems"), counts.TotalCount.ToString("N0"));
            Row(Loc.T("Dialogs.UnreadItems"), counts.UnreadCount.ToString("N0"));
            if (node.Account.Kind != AccountKind.Local)
            {
                Row("UIDVALIDITY", counts.UidValidity.ToString());
                Row(Loc.T("Dialogs.LastUid"), counts.LastUid.ToString());
            }
        }
        var close = new Button { Content = Loc.T("Common.Close"), Style = (Style)Application.Current.FindResource("PrimaryButton"), IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), MinWidth = 80 };
        close.Click += (_, _) => w.Close();
        w.Content = new StackPanel { Margin = new Thickness(18), Children = { grid, close } };
        w.ShowDialog();
    }
}

public static class AboutDialog
{
    public static void Show(Window? owner)
    {
        var w = DialogHelper.Create(owner, Loc.T("Main.AboutImvello"), 420);
        var close = new Button { Content = Loc.T("Common.OK"), Style = (Style)Application.Current.FindResource("PrimaryButton"), IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), MinWidth = 80 };
        close.Click += (_, _) => w.Close();
        var version = typeof(AboutDialog).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var icon = new TextBlock { Text = "\uE715", Style = (Style)Application.Current.FindResource("Icon"), FontSize = 40, HorizontalAlignment = HorizontalAlignment.Left };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        var versionText = new TextBlock { Text = Loc.F("Dialogs.VersionPrefix", version) };
        versionText.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        var dataText = new TextBlock { Text = Loc.F("Dialogs.DataFolderLabel", AppServices.Settings.DataDirectory), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        dataText.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        w.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                icon,
                new TextBlock { Text = "Imvello", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                versionText,
                new TextBlock { Text = Loc.T("Dialogs.AboutDescription"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) },
                dataText,
                close
            }
        };
        w.ShowDialog();
    }
}
