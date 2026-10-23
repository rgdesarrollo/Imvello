using System.IO;
using System.Windows;
using System.Windows.Controls;
using Imvello.Models;
using Imvello.Services;

namespace Imvello.Views;

public partial class Backstage : UserControl
{
    public Backstage() => InitializeComponent();

    private MainWindow? Main => Window.GetWindow(this) as MainWindow;
    private Account? SelectedAccount => AccountBox.SelectedItem as Account;

    public void Open()
    {
        Refresh();
        InfoNav.IsChecked = true;
        Visibility = Visibility.Visible;
        Focus();
    }

    public void Refresh()
    {
        var current = SelectedAccount?.Id;
        var accounts = AppServices.Accounts.GetAccounts().Where(a => a.Kind != AccountKind.Local).ToList();
        AccountBox.ItemsSource = accounts;
        AccountBox.SelectedItem = accounts.FirstOrDefault(a => a.Id == current) ?? accounts.FirstOrDefault();
        try
        {
            var db = new FileInfo(Path.Combine(AppServices.Settings.DataDirectory, "imvello.db"));
            var size = db.Exists ? SizeConverter.Format(db.Length) : "-";
            StorageText.Text = Loc.F("Main.StorageInfo", size, AppServices.Settings.DataDirectory);
        }
        catch { StorageText.Text = ""; }
    }

    private void AccountBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReconnectTile == null) return;
        ReconnectTile.Visibility = SelectedAccount is { } a && AccountService.CanReconnect(a) ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Close() => Visibility = Visibility.Collapsed;

    private void Back_Click(object sender, RoutedEventArgs e) => Close();

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (InfoPanel == null) return;
        InfoPanel.Visibility = sender == InfoNav ? Visibility.Visible : Visibility.Collapsed;
        OpenPanel.Visibility = sender == OpenNav ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Run(Action<MainWindow> action)
    {
        if (Main is not { } m) return;
        Close();
        action(m);
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e) => Run(m => m.AddAccount());
    private void AccountSettings_Click(object sender, RoutedEventArgs e) { if (SelectedAccount is { } a) Run(m => m.OpenAccountSettings(a)); }
    private void ReconnectAccount_Click(object sender, RoutedEventArgs e) { if (SelectedAccount is { } a && AccountService.CanReconnect(a)) Run(m => m.ReconnectAccount(a)); }
    private void SyncAccount_Click(object sender, RoutedEventArgs e) { if (SelectedAccount is { } a) Run(m => m.SyncAccount(a.Id)); }
    private void RemoveAccount_Click(object sender, RoutedEventArgs e) { if (SelectedAccount is { } a) Run(m => m.RemoveAccount(a)); }
    private void ImportThunderbird_Click(object sender, RoutedEventArgs e) => Run(m => m.ImportThunderbird());
    private void ImportMbox_Click(object sender, RoutedEventArgs e) => Run(m => m.ImportMbox(null));
    private void ExportMbox_Click(object sender, RoutedEventArgs e) => Run(m => m.ExportMbox(null));
    private void DataFolder_Click(object sender, RoutedEventArgs e) => Run(m => m.OpenDataFolder());
    private void Options_Click(object sender, RoutedEventArgs e) { ((RadioButton)sender).IsChecked = false; Run(m => m.OpenOptions()); }
    private void About_Click(object sender, RoutedEventArgs e) { ((RadioButton)sender).IsChecked = false; AboutDialog.Show(Main); }
    private void Exit_Click(object sender, RoutedEventArgs e) => Main?.Close();
}
