using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Imvello.Services;
using Imvello.ViewModels;

namespace Imvello.Views;

public partial class RibbonBar : UserControl
{
    public RibbonBar()
    {
        InitializeComponent();
        Tabs.SelectionChanged += (_, _) =>
        {
            var panels = new FrameworkElement[] { HomePanel, SendReceivePanel, FolderPanel, ViewPanel, HelpPanel };
            for (int i = 0; i < panels.Length; i++)
                panels[i].Visibility = i == Tabs.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void SaveSettings_Click(object sender, RoutedEventArgs e) => Vm?.Settings.Save();

    private static void ShowMenu(object sender, ContextMenu menu)
    {
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private MenuItem Item(string header, bool isChecked, Action action)
    {
        var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        mi.Click += (_, _) => action();
        return mi;
    }

    private void FilterMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var m = new ContextMenu();
        m.Items.Add(Item(Loc.T("Main.All"), vm.Filter == "All", () => vm.Filter = "All"));
        m.Items.Add(Item(Loc.T("Main.UnreadFilter"), vm.Filter == "Unread", () => vm.Filter = "Unread"));
        m.Items.Add(Item(Loc.T("Main.Flagged"), vm.Filter == "Flagged", () => vm.Filter = "Flagged"));
        ShowMenu(sender, m);
    }

    private void SortMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var m = new ContextMenu();
        foreach (var s in new[] { "Date", "From", "Subject", "Size" })
            m.Items.Add(Item(SortLabel(s), vm.SortBy == s, () => vm.SortBy = s));
        m.Items.Add(new Separator());
        m.Items.Add(Item(Loc.T("Main.NewestOnTop"), vm.SortDescending, () => vm.SortDescending = true));
        m.Items.Add(Item(Loc.T("Main.OldestOnTop"), !vm.SortDescending, () => vm.SortDescending = false));
        ShowMenu(sender, m);
    }

    private void ReadingPaneMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var m = new ContextMenu();
        foreach (var p in new[] { "Right", "Bottom", "Off" })
            m.Items.Add(Item(p switch { "Right" => Loc.T("Main.PaneRight"), "Bottom" => Loc.T("Main.PaneBottom"), _ => Loc.T("Main.PaneOff") }, vm.ReadingPanePosition == p, () => vm.ReadingPanePosition = p));
        ShowMenu(sender, m);
    }

    private static string SortLabel(string s) => s switch
    {
        "Date" => Loc.T("Main.SortByDate"),
        "From" => Loc.T("Main.SortByFrom"),
        "Subject" => Loc.T("Main.SortBySubject"),
        "Size" => Loc.T("Main.SortBySize"),
        _ => s
    };
}
