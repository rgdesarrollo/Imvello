using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Imvello.Services;
using Imvello.ViewModels;

namespace Imvello.Views;

public partial class MainWindow : Window
{
    private double _folderWidth = 240;

    public MainViewModel Vm { get; }

    public MainWindow()
    {
        Vm = new MainViewModel();
        InitializeComponent();
        DataContext = Vm;
        RegisterCommands();
        Vm.PropertyChanged += Vm_PropertyChanged;
        Width = Math.Max(MinWidth, Vm.Settings.WindowWidth);
        Height = Math.Max(MinHeight, Vm.Settings.WindowHeight);
        if (Vm.Settings.WindowMaximized) WindowState = WindowState.Maximized;
        Loaded += (_, _) =>
        {
            ApplyFolderPane();
            ApplyReadingPane();
            Vm.Initialize();
            UpdateTitle();
        };
        Loc.LanguageChanged += (_, _) => UpdateTitle();
        AppServices.Sync.ReauthRequired += (_, e) => Dispatcher.BeginInvoke(() => OnReauthRequired(e.AccountId));
        Closing += MainWindow_Closing;
        Closed += (_, _) => Application.Current.Shutdown();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        var s = Vm.Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal) { s.WindowWidth = ActualWidth; s.WindowHeight = ActualHeight; }
        try { s.Save(); } catch { }
        try { Services.AppServices.Sync.Stop(); } catch { }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ReadingPanePosition): ApplyReadingPane(); break;
            case nameof(MainViewModel.ShowFolderPane): ApplyFolderPane(); break;
            case nameof(MainViewModel.SelectedNode):
            case nameof(MainViewModel.IsSearchActive): UpdateTitle(); break;
        }
    }

    private void UpdateTitle()
    {
        if (_module != "Mail") return;
        var name = Vm.IsSearchActive ? Loc.T("Main.SearchResults") : Vm.SelectedNode?.Name ?? Loc.T("Main.Mail");
        Title = Loc.F("Main.WindowTitleFormat", name);
    }

    private void ApplyFolderPane()
    {
        if (Vm.ShowFolderPane)
        {
            FolderColumn.Width = new GridLength(Math.Max(180, _folderWidth));
            FolderPaneView.Visibility = FolderSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            if (FolderColumn.ActualWidth > 0) _folderWidth = FolderColumn.ActualWidth;
            FolderColumn.Width = new GridLength(0);
            FolderPaneView.Visibility = FolderSplitter.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyReadingPane()
    {
        var g = ListReadingGrid;
        g.RowDefinitions.Clear();
        g.ColumnDefinitions.Clear();
        foreach (var c in new UIElement[] { MessageListView, ReadingSplitter, ReadingPaneView })
        {
            Grid.SetRow(c, 0);
            Grid.SetColumn(c, 0);
        }
        var pos = Vm.ReadingPanePosition;
        var on = pos is "Right" or "Bottom";
        ReadingSplitter.Visibility = ReadingPaneView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ReadingSplitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
        if (pos == "Bottom")
        {
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.4, GridUnitType.Star), MinHeight = 120 });
            Grid.SetRow(ReadingSplitter, 1);
            Grid.SetRow(ReadingPaneView, 2);
            ReadingSplitter.ResizeDirection = GridResizeDirection.Rows;
            ReadingSplitter.Width = double.NaN;
            ReadingSplitter.Height = 4;
            ReadingSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            ReadingSplitter.VerticalAlignment = VerticalAlignment.Center;
        }
        else if (pos == "Right")
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star), MinWidth = 280 });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star), MinWidth = 300 });
            Grid.SetColumn(ReadingSplitter, 1);
            Grid.SetColumn(ReadingPaneView, 2);
            ReadingSplitter.ResizeDirection = GridResizeDirection.Columns;
            ReadingSplitter.Height = double.NaN;
            ReadingSplitter.Width = 4;
            ReadingSplitter.HorizontalAlignment = HorizontalAlignment.Center;
            ReadingSplitter.VerticalAlignment = VerticalAlignment.Stretch;
        }
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await Vm.RunSearchAsync(); }
        else if (e.Key == Key.Escape) { e.Handled = true; await Vm.ClearSearchAsync(); MessageListView.Focus(); }
    }

    private async void ClearSearch_Click(object sender, RoutedEventArgs e) => await Vm.ClearSearchAsync();

    private async void SearchScope_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Vm == null || sender is not ComboBox cb) return;
        Vm.SearchAllMailboxes = cb.SelectedIndex == 0;
        if (Vm.IsSearchActive) await Vm.RunSearchAsync();
    }

    private string _module = "Mail";

    private void NavMail_Click(object sender, RoutedEventArgs e) => ShowModule("Mail");
    private void NavCalendar_Click(object sender, RoutedEventArgs e) => ShowModule("Calendar");
    private void NavPeople_Click(object sender, RoutedEventArgs e) => ShowModule("People");

    public void ShowModule(string module)
    {
        _module = module;
        var mail = module == "Mail";
        NavMail.IsChecked = mail;
        NavCalendar.IsChecked = module == "Calendar";
        NavPeople.IsChecked = module == "People";
        CalendarModule.Visibility = module == "Calendar" ? Visibility.Visible : Visibility.Collapsed;
        ContactsModule.Visibility = module == "People" ? Visibility.Visible : Visibility.Collapsed;
        var mailVis = mail ? Visibility.Visible : Visibility.Collapsed;
        Ribbon.Visibility = SearchArea.Visibility = SearchScopePanel.Visibility = mailVis;
        if (module == "Calendar") CalendarModule.Activate();
        else if (module == "People") ContactsModule.Activate();
        if (mail) UpdateTitle();
        else Title = Loc.F("Main.WindowTitleFormat", Loc.T(module == "Calendar" ? "Main.Calendar" : "Main.People"));
    }

    private void NotAvailable_Click(object sender, RoutedEventArgs e)
    {
        var name = (sender as FrameworkElement)?.ToolTip as string ?? Loc.T("Main.ThisModule");
        MessageBox.Show(this, Loc.F("Main.ComingSoon", name), "Imvello", MessageBoxButton.OK, MessageBoxImage.Information);
        if (sender is RadioButton rb) rb.IsChecked = false;
        ShowModule(_module);
    }
}
