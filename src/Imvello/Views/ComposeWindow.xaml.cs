using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Imvello.Models;
using Imvello.Services;
using Microsoft.Win32;
using MimeKit;

namespace Imvello.Views;

public record ComposeAttachment(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string SizeText => File.Exists(Path) ? SizeConverter.Format(new FileInfo(Path).Length) : "";
}

public partial class ComposeWindow : Window
{
    private const string Host = "imvello.local";
    private static readonly (string label, int value)[] Sizes = [("8", 1), ("10", 2), ("11", 3), ("14", 4), ("18", 5), ("24", 6), ("36", 7)];
    private readonly string _fileId = Guid.NewGuid().ToString("N");
    private readonly System.Collections.ObjectModel.ObservableCollection<ComposeAttachment> _attachments = new();
    private OutgoingMessage _msg = new();
    private bool _editorReady;
    private bool _closeConfirmed;
    private bool _busy;
    private string _initialHtml = "";

    public ComposeWindow()
    {
        InitializeComponent();
        RecipientAutoComplete.Attach(ToBox);
        RecipientAutoComplete.Attach(CcBox);
        RecipientAutoComplete.Attach(BccBox);
        AttachmentList.ItemsSource = _attachments;
        FontBox.ItemsSource = new[] { "Calibri", "Arial", "Segoe UI", "Times New Roman", "Verdana", "Georgia", "Tahoma", "Courier New" };
        SizeBox.ItemsSource = Sizes.Select(s => s.label).ToList();
        var accounts = AppServices.Accounts.GetAccounts().Where(a => a.Kind != AccountKind.Local).ToList();
        FromBox.ItemsSource = accounts;
        FromBox.SelectionChanged += FromBox_SelectionChanged;
        if (accounts.Count <= 1) { FromToggle.IsChecked = false; SetFromVisible(false); }
        Tabs.SelectionChanged += (_, _) =>
        {
            var panels = new FrameworkElement[] { MessagePanel, InsertPanel, OptionsPanel, FormatPanel };
            for (int i = 0; i < panels.Length; i++) panels[i].Visibility = i == Tabs.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        };
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, async (_, e) =>
        {
            if (e.Parameter as string == "send") await SendAsync();
            else await SaveDraftAsync(false);
        }));
        AllowDrop = true;
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                foreach (var f in files.Where(File.Exists)) _attachments.Add(new ComposeAttachment(f));
        };
        Unloaded += (_, _) => { try { File.Delete(EditorFile); } catch { } };
    }

    private string EditorFile => Path.Combine(ReadingPane.ViewFolder, "c" + _fileId + ".html");

    public static void ShowCompose(ComposeMode mode, long? messageId, string? to = null)
    {
        if (!AppServices.Accounts.GetAccounts().Any(a => a.Kind != AccountKind.Local))
        {
            MessageBox.Show(Loc.T("Dialogs.AddAccountToSend"), "Imvello", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var w = new ComposeWindow();
        if (Application.Current.MainWindow is { IsVisible: true } owner && owner != w) w.Owner = owner;
        w.Show();
        _ = w.LoadAsync(mode, messageId, to);
    }

    private async Task LoadAsync(ComposeMode mode, long? messageId, string? to)
    {
        try
        {
            if (mode == ComposeMode.New || messageId == null)
            {
                var vm = (Owner as MainWindow)?.DataContext as ViewModels.MainViewModel;
                var accounts = (List<Account>)FromBox.ItemsSource;
                var accountId = vm?.SelectedNode?.Account is { Kind: not AccountKind.Local } a ? a.Id : accounts[0].Id;
                _msg = AppServices.Compose.CreateNew(accountId);
                if (!string.IsNullOrWhiteSpace(to)) _msg.To.Add(to);
            }
            else
            {
                _msg = await Task.Run(() => AppServices.Compose.CreateAsync(mode, messageId.Value));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.F("Dialogs.CouldNotPrepare", ex.Message), "Imvello", MessageBoxButton.OK, MessageBoxImage.Warning);
            _closeConfirmed = true;
            Close();
            return;
        }

        FromBox.SelectedItem = ((List<Account>)FromBox.ItemsSource).FirstOrDefault(a => a.Id == _msg.AccountId) ?? ((List<Account>)FromBox.ItemsSource).FirstOrDefault();
        ToBox.Text = string.Join("; ", _msg.To);
        CcBox.Text = string.Join("; ", _msg.Cc);
        BccBox.Text = string.Join("; ", _msg.Bcc);
        if (_msg.Bcc.Count > 0) { BccToggle.IsChecked = true; SetBccVisible(true); }
        SubjectBox.Text = _msg.Subject;
        HighToggle.IsChecked = _msg.Importance == 2;
        LowToggle.IsChecked = _msg.Importance == 0;
        foreach (var p in _msg.AttachmentPaths) _attachments.Add(new ComposeAttachment(p));
        FontBox.SelectedItem = "Calibri";
        SizeBox.SelectedItem = "11";
        await InitEditorAsync(_msg.HtmlBody);
        if (mode is ComposeMode.Reply or ComposeMode.ReplyAll) FocusEditor();
        else if (ToBox.Text.Length == 0) ToBox.Focus();
        else FocusEditor();
    }

    private async Task InitEditorAsync(string html)
    {
        try
        {
            var env = await ReadingPane.GetEnvironmentAsync();
            await Editor.EnsureCoreWebView2Async(env);
            var core = Editor.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            Directory.CreateDirectory(ReadingPane.ViewFolder);
            core.SetVirtualHostNameToFolderMapping(Host, ReadingPane.ViewFolder, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase)) return;
                e.Cancel = true;
                if (e.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    try { var p = new Uri(e.Uri).LocalPath; if (File.Exists(p)) Dispatcher.BeginInvoke(() => _attachments.Add(new ComposeAttachment(p))); } catch { }
                }
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            var tcs = new TaskCompletionSource();
            void Done(object? s, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e) { core.NavigationCompleted -= Done; tcs.TrySetResult(); }
            core.NavigationCompleted += Done;
            var doc = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>html,body{height:100%}body{font-family:Calibri,'Segoe UI',sans-serif;font-size:11pt;margin:0;padding:10px 14px;outline:none;word-wrap:break-word;box-sizing:border-box}img{max-width:100%}</style></head>" +
                      "<body contenteditable=\"true\" spellcheck=\"true\">" + html + "</body></html>";
            await File.WriteAllTextAsync(EditorFile, doc, new UTF8Encoding(true));
            core.Navigate($"https://{Host}/c{_fileId}.html");
            await tcs.Task;
            await core.ExecuteScriptAsync("document.execCommand('styleWithCSS',false,true);document.body.focus();");
            _editorReady = true;
            _initialHtml = await GetBodyHtmlAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            Editor.Visibility = Visibility.Collapsed;
            FallbackEditor.Visibility = Visibility.Visible;
            FallbackEditor.Text = HtmlUtil.HtmlToText(html);
            _initialHtml = await GetBodyHtmlAsync();
        }
        finally { BusyText.Visibility = Visibility.Collapsed; }
    }

    private void FocusEditor()
    {
        if (_editorReady) { Editor.Focus(); _ = Editor.CoreWebView2.ExecuteScriptAsync("document.body.focus()"); }
        else FallbackEditor.Focus();
    }

    private async Task<string> GetBodyHtmlAsync()
    {
        if (!_editorReady) return HtmlUtil.TextToHtml(FallbackEditor.Text);
        var json = await Editor.CoreWebView2.ExecuteScriptAsync("document.body.innerHTML");
        return JsonSerializer.Deserialize<string>(json) ?? "";
    }

    private async Task ExecAsync(string command, string? arg = null)
    {
        if (!_editorReady) return;
        if (command == "paste")
        {
            if (Clipboard.ContainsText()) await ExecAsync("insertText", Clipboard.GetText());
            return;
        }
        await Editor.CoreWebView2.ExecuteScriptAsync($"document.body.focus();document.execCommand({JsonSerializer.Serialize(command)},false,{(arg == null ? "null" : JsonSerializer.Serialize(arg))})");
        Editor.Focus();
    }

    private async void Exec_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string cmd }) await ExecAsync(cmd);
    }

    private async void Color_Click(object sender, RoutedEventArgs e) => await ExecAsync("foreColor", (sender as FrameworkElement)?.Tag as string ?? "#C50F1F");
    private async void Highlight_Click(object sender, RoutedEventArgs e) => await ExecAsync("hiliteColor", "#FFF100");

    private async void FontBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FontBox.SelectedItem is string f && _editorReady && e.RemovedItems.Count > 0) await ExecAsync("fontName", f);
    }

    private async void SizeBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SizeBox.SelectedItem is string s && _editorReady && e.RemovedItems.Count > 0)
            await ExecAsync("fontSize", Sizes.First(x => x.label == s).value.ToString());
    }

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = Loc.T("Dialogs.InsertFile"), Filter = Loc.T("Common.AllFilesFilter") };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames) _attachments.Add(new ComposeAttachment(f));
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ComposeAttachment a) _attachments.Remove(a);
    }

    private async void Picture_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = Loc.T("Dialogs.InsertPicture"), Filter = Loc.T("Dialogs.ImagesFilter") };
        if (dlg.ShowDialog(this) != true) return;
        var info = new FileInfo(dlg.FileName);
        if (info.Length > 10 * 1024 * 1024) { MessageBox.Show(this, Loc.T("Dialogs.PictureTooLarge"), "Imvello"); return; }
        var ext = info.Extension.TrimStart('.').ToLowerInvariant();
        var mime = ext switch { "jpg" or "jpeg" => "image/jpeg", "gif" => "image/gif", "bmp" => "image/bmp", "webp" => "image/webp", _ => "image/png" };
        var data = Convert.ToBase64String(await File.ReadAllBytesAsync(dlg.FileName));
        await ExecAsync("insertImage", $"data:{mime};base64,{data}");
    }

    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        var url = InputDialog.Show(this, Loc.T("Dialogs.InsertHyperlink"), Loc.T("Dialogs.AddressLabel"), "https://");
        if (string.IsNullOrWhiteSpace(url) || url == "https://") return;
        await ExecAsync("createLink", url.Trim());
    }

    private async void Signature_Click(object sender, RoutedEventArgs e)
    {
        var sig = FromBox.SelectedItem is Account a ? AppServices.Compose.SignatureHtml(a.Id) : "";
        if (sig.Length == 0) { MessageBox.Show(this, Loc.T("Dialogs.NoSignature"), "Imvello"); return; }
        await ExecAsync("insertHTML", sig);
    }

    private void Importance_Click(object sender, RoutedEventArgs e)
    {
        if (sender == HighToggle && HighToggle.IsChecked == true) LowToggle.IsChecked = false;
        if (sender == LowToggle && LowToggle.IsChecked == true) HighToggle.IsChecked = false;
    }

    private void SetBccVisible(bool v) => BccBox.Visibility = BccLabel.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
    private void SetFromVisible(bool v) => FromBox.Visibility = FromLabel.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
    private void BccToggle_Click(object sender, RoutedEventArgs e) => SetBccVisible(BccToggle.IsChecked == true);
    private void FromToggle_Click(object sender, RoutedEventArgs e) => SetFromVisible(FromToggle.IsChecked == true);

    private void FromBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FromBox.SelectedItem is Account a) _msg.AccountId = a.Id;
    }

    private void SubjectBox_TextChanged(object sender, TextChangedEventArgs e) =>
        Title = Loc.F("Dialogs.ComposeTitleFormat", string.IsNullOrWhiteSpace(SubjectBox.Text) ? Loc.T("Dialogs.Untitled") : SubjectBox.Text);

    private static List<string> ParseList(string text, List<string> errors)
    {
        var result = new List<string>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (InternetAddressList.TryParse(part, out var list) && list.Mailboxes.Any() && list.Mailboxes.All(m => m.Address.Contains('@')))
                result.Add(list.ToString());
            else errors.Add(part);
        }
        return result;
    }

    private void CheckNames_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        ToBox.Text = string.Join("; ", ParseList(ToBox.Text.Replace(',', ';'), errors));
        CcBox.Text = string.Join("; ", ParseList(CcBox.Text.Replace(',', ';'), errors));
        BccBox.Text = string.Join("; ", ParseList(BccBox.Text.Replace(',', ';'), errors));
        if (errors.Count > 0)
            MessageBox.Show(this, Loc.F("Dialogs.NamesNotResolved", string.Join("\n", errors)), Loc.T("Dialogs.CheckNames"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task<OutgoingMessage?> BuildAsync()
    {
        var errors = new List<string>();
        _msg.To = ParseList(ToBox.Text, errors);
        _msg.Cc = ParseList(CcBox.Text, errors);
        _msg.Bcc = BccBox.Visibility == Visibility.Visible ? ParseList(BccBox.Text, errors) : new List<string>();
        if (errors.Count > 0)
        {
            MessageBox.Show(this, Loc.F("Dialogs.UnrecognizedAddresses", string.Join("\n", errors)), "Imvello", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        if (FromBox.SelectedItem is Account a) _msg.AccountId = a.Id;
        _msg.Subject = SubjectBox.Text;
        _msg.HtmlBody = await GetBodyHtmlAsync();
        _msg.AttachmentPaths = _attachments.Select(x => x.Path).ToList();
        _msg.Importance = HighToggle.IsChecked == true ? 2 : LowToggle.IsChecked == true ? 0 : 1;
        return _msg;
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        SendButton.IsEnabled = !busy;
        Cursor = busy ? Cursors.Wait : null;
        if (text != null) Title = text;
        else SubjectBox_TextChanged(this, null!);
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        if (_busy) return;
        var m = await BuildAsync();
        if (m == null) return;
        if (m.To.Count + m.Cc.Count + m.Bcc.Count == 0)
        {
            MessageBox.Show(this, Loc.T("Dialogs.NoRecipients"), "Imvello", MessageBoxButton.OK, MessageBoxImage.Warning);
            ToBox.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(m.Subject) &&
            MessageBox.Show(this, Loc.T("Dialogs.NoSubjectSendConfirm"), "Imvello", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var missing = m.AttachmentPaths.FirstOrDefault(p => !File.Exists(p));
        if (missing != null) { MessageBox.Show(this, Loc.F("Dialogs.AttachmentNotFound", missing), "Imvello"); return; }
        SetBusy(true, Loc.T("Dialogs.Sending"));
        try
        {
            await Task.Run(() => AppServices.Send.SendAsync(m));
            _closeConfirmed = true;
            Close();
        }
        catch (Exception ex)
        {
            SetBusy(false);
            MessageBox.Show(this, Loc.F("Dialogs.SendFailed", ex.Message), "Imvello", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<bool> SaveDraftAsync(bool closing)
    {
        if (_busy) return false;
        var m = await BuildAsync();
        if (m == null) return false;
        SetBusy(true, Loc.T("Dialogs.SavingDraft"));
        try
        {
            var id = await Task.Run(() => AppServices.Send.SaveDraftAsync(m));
            m.DraftMessageId = id;
            _initialHtml = m.HtmlBody;
            SetBusy(false);
            return true;
        }
        catch (Exception ex)
        {
            SetBusy(false);
            MessageBox.Show(this, Loc.F("Dialogs.DraftSaveFailed", ex.Message), "Imvello", MessageBoxButton.OK, MessageBoxImage.Warning);
            return !closing ? false : MessageBox.Show(this, Loc.T("Dialogs.CloseWithoutSaving"), "Imvello", MessageBoxButton.YesNo) == MessageBoxResult.Yes;
        }
    }

    private async Task<bool> IsDirtyAsync()
    {
        if (ToBox.Text.Trim().Length > 0 && ToBox.Text != string.Join("; ", _msg.To)) return true;
        if (SubjectBox.Text != _msg.Subject || _attachments.Count != _msg.AttachmentPaths.Count) return true;
        try { return (await GetBodyHtmlAsync()) != _initialHtml; } catch { return false; }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed) return;
        e.Cancel = true;
        if (_busy) return;
        if (!await IsDirtyAsync()) { _closeConfirmed = true; Close(); return; }
        var r = MessageBox.Show(this, Loc.T("Dialogs.SaveChangesPrompt"), "Imvello", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return;
        if (r == MessageBoxResult.Yes && !await SaveDraftAsync(true)) return;
        _closeConfirmed = true;
        Close();
    }
}
