using System.Diagnostics;
using System.IO;
using System.Windows;
using Imvello.Models;
using Imvello.Services;
using Imvello.ViewModels;
using Microsoft.Win32;

namespace Imvello.Views;

public partial class MainWindow
{
    public void OpenMessage(MessageItem item)
    {
        if (Vm.IsDraftsFolder || item.Header.Flags.HasFlag(MessageFlags.Draft))
        {
            ComposeWindow.ShowCompose(ComposeMode.EditDraft, item.Id);
            return;
        }
        var model = new MessageViewerModel();
        var w = new Window
        {
            Title = Loc.F("Main.MessageWindowTitle", item.Subject),
            Width = 980,
            Height = 760,
            Icon = Icon,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Style = (Style)Application.Current.FindResource("ImvelloWindow"),
            Content = new ReadingPane { DataContext = model }
        };
        w.Show();
        _ = model.LoadAsync(item.Id);
        if (!item.IsRead && Vm.Settings.MarkAsReadWhenViewed)
        {
            item.IsRead = true;
            _ = Vm.RunActionAsync(() => AppServices.Messages.SetReadAsync([item.Id], true), refresh: false);
        }
    }

    public void PermanentDeleteSelected()
    {
        var n = Vm.SelectedItems.Count;
        if (n == 0 || !Confirm(Loc.F("Main.PermanentDeleteConfirm", n))) return;
        _ = Vm.PermanentDeleteSelectedAsync();
    }

    public void AddAccount()
    {
        var a = AddAccountWindow.ShowFor(this);
        if (a == null) return;
        BackstageView.Close();
        Vm.LoadFolders();
        Vm.StatusText = a.Email + " " + Loc.T("Main.AccountAddedSuffix");
    }

    public void OpenAccountSettings(Account account)
    {
        if (AccountSettingsWindow.Show(this, account)) { Vm.LoadFolders(); BackstageView.Refresh(); }
    }

    private readonly HashSet<long> _reauthPrompted = new();
    private bool _reconnecting;

    /// <summary>Called when background sync finds an expired/revoked Google or Microsoft sign-in. Asks once per account per session.</summary>
    private void OnReauthRequired(long accountId)
    {
        if (_reconnecting || !_reauthPrompted.Add(accountId)) return;
        if (AppServices.Db.GetAccount(accountId) is not { } a) return;
        if (MessageBox.Show(this, Loc.F("Main.ReauthPrompt", a.Email, ProviderName(a)), Loc.T("Main.ReconnectTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            ReconnectAccount(a);
    }

    private static string ProviderName(Account a) => a.Kind == AccountKind.Outlook ? "Microsoft" : "Google";

    public async void ReconnectAccount(Account account)
    {
        if (_reconnecting) return;
        _reconnecting = true;
        try
        {
            await RunAsync(Loc.F("Main.ReconnectWaiting", ProviderName(account)), async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                try { await AppServices.Accounts.ReconnectAsync(account.Id, timeout.Token); }
                catch (Exception ex) when (timeout.IsCancellationRequested && ex is OperationCanceledException or System.Net.HttpListenerException or ObjectDisposedException)
                {
                    throw new TimeoutException(Loc.T("Main.ReconnectTimedOut"));
                }
            }, Loc.F("Main.ReconnectDone", account.Email));
            _reauthPrompted.Remove(account.Id);
            Activate();
            Vm.LoadFolders();
            BackstageView.Refresh();
        }
        finally { _reconnecting = false; }
    }

    public async void RemoveAccount(Account account)
    {
        if (!Confirm(Loc.F("Main.RemoveAccountConfirm", account.Email), Loc.T("Main.RemoveAccount"))) return;
        await RunAsync(Loc.F("Main.RemovingAccount", account.Email), () => AppServices.Accounts.RemoveAccountAsync(account.Id), Loc.T("Main.AccountRemoved"));
        Vm.LoadFolders();
        BackstageView.Refresh();
    }

    public async void SyncAccount(long accountId) =>
        await RunAsync(Loc.T("Main.UpdatingAccount"), () => AppServices.Sync.SyncAccountAsync(accountId, CancellationToken.None));

    public async void SyncFolder(long folderId) =>
        await RunAsync(Loc.T("Main.UpdatingFolder"), () => AppServices.Sync.SyncFolderAsync(folderId, CancellationToken.None), Loc.T("Main.FolderUpToDate"));

    public async void NewFolder(FolderNode? node)
    {
        node ??= Vm.SelectedNode;
        if (node == null) return;
        var name = InputDialog.Show(this, Loc.T("Main.NewFolderTitle"), Loc.T("Main.FolderNamePrompt"))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        await RunAsync(Loc.F("Main.CreatingFolder", name), () => AppServices.Sync.CreateFolderAsync(node.Account.Id, node.Folder?.FullName, name), Loc.T("Main.FolderCreated"));
        Vm.LoadFolders();
    }

    public async void RenameFolder(FolderNode node)
    {
        if (node.Folder is not { } f) return;
        var name = InputDialog.Show(this, Loc.T("Main.RenameFolderTitle"), Loc.T("Main.NewFolderNamePrompt"), f.DisplayName)?.Trim();
        if (string.IsNullOrEmpty(name) || name == f.DisplayName) return;
        await RunAsync(Loc.T("Main.RenamingFolder"), () => AppServices.Sync.RenameFolderAsync(f.Id, name), Loc.T("Main.FolderRenamed"));
        Vm.LoadFolders();
    }

    public async void DeleteFolder(FolderNode node)
    {
        if (node.Folder is not { } f) return;
        if (!Confirm(Loc.F("Main.DeleteFolderConfirm", node.Name), Loc.T("Main.DeleteFolderTitle"))) return;
        await RunAsync(Loc.T("Main.DeletingFolder"), () => AppServices.Sync.DeleteFolderAsync(f.Id), Loc.T("Main.FolderDeleted"));
        Vm.LoadFolders();
    }

    public async void MarkAllRead(FolderNode node)
    {
        if (node.Folder is not { } f) return;
        await RunAsync(Loc.T("Main.MarkingAllAsRead"), () => AppServices.Sync.MarkAllReadAsync(f.Id), Loc.T("Main.Ready"));
        if (Vm.SelectedNode?.Folder?.Id == f.Id) await Vm.ReloadMessagesAsync(true);
    }

    public async void EmptyFolder(FolderNode node)
    {
        if (node.Folder is not { } f) return;
        if (!Confirm(Loc.F("Main.EmptyFolderConfirm", node.Name), Loc.T("Main.DeleteAll"))) return;
        await RunAsync(Loc.T("Main.DeletingAllItems"), () => AppServices.Sync.EmptyFolderAsync(f.Id), Loc.T("Main.Ready"));
        if (Vm.SelectedNode?.Folder?.Id == f.Id) await Vm.ReloadMessagesAsync(false);
    }

    public async void ImportThunderbird()
    {
        var profiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Thunderbird", "Profiles");
        var dlg = new OpenFolderDialog { Title = Loc.T("Main.ThunderbirdPickerTitle") };
        if (Directory.Exists(profiles)) dlg.InitialDirectory = profiles;
        if (dlg.ShowDialog(this) != true) return;
        BackstageView.Close();
        var progress = new Progress<string>(s => Vm.StatusText = s);
        long count = 0;
        await RunAsync(Loc.T("Main.ImportingThunderbird"), async () =>
            count = await AppServices.Mbox.ImportThunderbirdDirectoryAsync(dlg.FolderName, AppServices.Accounts.LocalAccount.Id, null, progress, CancellationToken.None));
        Vm.LoadFolders();
        Vm.StatusText = Loc.F("Main.ImportedIntoLocalFolders", count);
        Info(Loc.F("Main.ImportedIntoLocalFolders", count));
    }

    public async void ImportMbox(FolderNode? node)
    {
        node ??= Vm.SelectedNode;
        if (node?.Folder is not { } f) { Info(Loc.T("Main.SelectFolderToImport")); return; }
        var dlg = new OpenFileDialog
        {
            Title = Loc.F("Main.ImportMboxTitle", node.Name),
            Filter = Loc.T("Main.ImportMboxFilter"),
            Multiselect = true
        };
        if (dlg.ShowDialog(this) != true) return;
        BackstageView.Close();
        var progress = new Progress<string>(s => Vm.StatusText = s);
        long total = 0;
        foreach (var file in dlg.FileNames)
        {
            if (!MboxService.LooksLikeMbox(file) && !Confirm(Loc.F("Main.NotMboxConfirm", Path.GetFileName(file)))) continue;
            await RunAsync(Loc.F("Main.ImportingFile", Path.GetFileName(file)), async () =>
                total += await AppServices.Mbox.ImportMboxAsync(file, f.Id, progress, CancellationToken.None));
        }
        Vm.LoadFolders();
        if (Vm.SelectedNode?.Folder?.Id == f.Id) await Vm.ReloadMessagesAsync(false);
        Vm.StatusText = Loc.F("Main.ImportedIntoFolder", total, node.Name);
    }

    public async void ExportMbox(FolderNode? node)
    {
        node ??= Vm.SelectedNode;
        if (node?.Folder is not { } f) { Info(Loc.T("Main.SelectFolderToExport")); return; }
        var dlg = new SaveFileDialog
        {
            Title = Loc.F("Main.ExportMboxTitle", node.Name),
            FileName = SafeName(node.Name) + ".mbox",
            Filter = Loc.T("Main.MboxFilter")
        };
        if (dlg.ShowDialog(this) != true) return;
        BackstageView.Close();
        var progress = new Progress<string>(s => Vm.StatusText = s);
        long count = 0;
        await RunAsync(Loc.F("Main.ExportingFolder", node.Name), async () =>
            count = await AppServices.Mbox.ExportFolderToMboxAsync(f.Id, dlg.FileName, progress, CancellationToken.None));
        Vm.StatusText = Loc.F("Main.ExportedToFile", count, Path.GetFileName(dlg.FileName));
    }

    public void ShowFolderProperties(FolderNode node) => FolderPropertiesDialog.Show(this, node);

    public void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Vm.Settings.DataDirectory}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Info(ex.Message); }
    }

    public void OpenOptions()
    {
        if (!OptionsWindow.Show(this)) return;
        Vm.ShowPreview = Vm.Settings.ShowPreview;
        _ = Vm.ReloadMessagesAsync(true);
    }

    private async void SaveSelectedAs()
    {
        if (Primary is not { } item) return;
        var dlg = new SaveFileDialog { FileName = SafeName(item.Subject) + ".eml", Filter = Loc.T("Main.EmlFilter") };
        if (dlg.ShowDialog(this) != true) return;
        await RunAsync(Loc.T("Main.SavingMessage"), () => AppServices.Messages.SaveMessageAsEmlAsync(item.Id, dlg.FileName), Loc.T("Main.MessageSaved"));
    }

    private static string SafeName(string s)
    {
        var name = string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return string.IsNullOrEmpty(name) ? "message" : name.Length > 80 ? name[..80] : name;
    }
}
