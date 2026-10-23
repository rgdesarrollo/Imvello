using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Imvello.Models;
using Imvello.Services;
using Imvello.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Imvello.Views;

public partial class ReadingPane : UserControl
{
    private const string Host = "imvello.local";
    private static Task<CoreWebView2Environment>? _env;
    private readonly string _fileId = Guid.NewGuid().ToString("N");
    private IReadingSource? _source;
    private Task<bool>? _init;
    private bool _allowRemote;
    private bool _blockedShown;
    private int _renderVersion;
    private MessageContent? _rendered;
    private string? _viewFile;
    private int _navSeq;

    public ReadingPane()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as IReadingSource);
        Unloaded += (_, _) => { _rendered = null; if (_viewFile != null) TryDelete(_viewFile); _viewFile = null; };
        Loaded += (_, _) => { if (_rendered == null) Update(); };
    }

    public static string ViewFolder => Path.Combine(AppServices.Settings.DataDirectory, "WebView2", "view");

    public static Task<CoreWebView2Environment> GetEnvironmentAsync() =>
        _env ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(AppServices.Settings.DataDirectory, "WebView2"));

    public MessageContent? Content => _source?.CurrentContent;

    private void Attach(IReadingSource? source)
    {
        if (_source != null) _source.PropertyChanged -= Source_PropertyChanged;
        _source = source;
        if (source != null) source.PropertyChanged += Source_PropertyChanged;
        Update();
    }

    private void Source_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IReadingSource.CurrentContent) or nameof(IReadingSource.IsLoadingContent) or nameof(IReadingSource.ContentError) or nameof(MainViewModel.SelectedItems) or nameof(MainViewModel.HasSelection))
            Update();
    }

    private void Show(FrameworkElement panel)
    {
        foreach (var p in new FrameworkElement[] { EmptyPanel, LoadingPanel, ErrorPanel, MessagePanel })
            p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Update()
    {
        var s = _source;
        if (s == null) { _rendered = null; Show(EmptyPanel); return; }
        if (s.ContentError != null) { ErrorText.Text = s.ContentError; _rendered = null; Show(ErrorPanel); return; }
        if (s.CurrentContent is { } c) { Show(MessagePanel); if (!ReferenceEquals(c, _rendered)) _ = RenderAsync(c); return; }
        if (s.IsLoadingContent) { _rendered = null; Show(LoadingPanel); return; }
        var count = (s as MainViewModel)?.SelectedItems.Count ?? 0;
        EmptyText.Text = count > 1 ? Loc.F("Main.ItemsSelectedLabel", count) : Loc.T("Main.SelectItemToRead");
        EmptySub.Text = count > 1 ? Loc.T("Main.SelectionHint") : Loc.T("Main.NothingSelected");
        _rendered = null;
        Show(EmptyPanel);
    }

    private async Task RenderAsync(MessageContent c)
    {
        var version = ++_renderVersion;
        _rendered = c;
        var h = c.Header;
        var item = new MessageItem(h);
        SubjectText.Text = item.Subject;
        Avatar.Background = item.AvatarBrush;
        AvatarText.Text = item.Initials;
        FromText.Text = string.IsNullOrEmpty(h.FromName) ? h.FromAddress : $"{h.FromName} <{h.FromAddress}>";
        ToText.Text = string.IsNullOrWhiteSpace(c.ToFull) ? "" : Loc.F("Main.ToField", c.ToFull);
        ToText.Visibility = ToText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CcText.Text = string.IsNullOrWhiteSpace(c.CcFull) ? "" : Loc.F("Main.CcField", c.CcFull);
        CcText.Visibility = CcText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DateText.Text = h.Date.LocalDateTime.ToString("ddd M/d/yyyy h:mm tt");
        AttachmentList.ItemsSource = c.Attachments;
        AttachmentList.Visibility = c.Attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _allowRemote = !AppServices.Settings.BlockRemoteImages;
        _blockedShown = false;
        ImagesBar.Visibility = Visibility.Collapsed;

        _init ??= InitBrowserAsync();
        if (!await _init)
        {
            Browser.Visibility = Visibility.Collapsed;
            FallbackText.Visibility = Visibility.Visible;
            FallbackText.Text = c.TextBody ?? HtmlUtil.HtmlToText(c.HtmlBody ?? "");
            return;
        }
        if (version != _renderVersion) return;
        await NavigateAsync(c);
    }

    private async Task NavigateAsync(MessageContent c)
    {
        var version = _renderVersion;
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><meta charset=\"utf-8\">");
        sb.Append("<style>html,body{font-family:'Segoe UI',Calibri,sans-serif;font-size:14px;margin:0;padding:10px 16px;word-wrap:break-word;color:#242424}img{max-width:100%;height:auto}blockquote{border-left:3px solid #ccc;margin-left:4px;padding-left:10px;color:#555}pre{white-space:pre-wrap}</style>");
        sb.Append(c.HtmlBody ?? "");
        var name = $"{_fileId}-{++_navSeq}.html";
        var path = Path.Combine(ViewFolder, name);
        Directory.CreateDirectory(ViewFolder);
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true));
        if (version != _renderVersion) { TryDelete(path); return; }
        var previous = _viewFile;
        _viewFile = path;
        Browser.CoreWebView2.Navigate($"https://{Host}/{name}");
        if (previous != null) TryDelete(previous);
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    private async Task<bool> InitBrowserAsync()
    {
        try
        {
            var env = await GetEnvironmentAsync();
            await Browser.EnsureCoreWebView2Async(env);
            var core = Browser.CoreWebView2;
            core.Settings.IsScriptEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = true;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            Directory.CreateDirectory(ViewFolder);
            core.SetVirtualHostNameToFolderMapping(Host, ViewFolder, CoreWebView2HostResourceAccessKind.Deny);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                var uri = e.Request.Uri;
                if (uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("data:") || uri.StartsWith("about:")) return;
                if (_allowRemote) return;
                e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "");
                if (!_blockedShown)
                {
                    _blockedShown = true;
                    Dispatcher.BeginInvoke(() => ImagesBar.Visibility = Visibility.Visible);
                }
            };
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("about:")) return;
                e.Cancel = true;
                if (e.IsUserInitiated) OpenExternal(e.Uri);
            };
            core.FrameNavigationStarting += (_, e) => { if (!_allowRemote) e.Cancel = true; };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            };
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    private void OpenExternal(string uri)
    {
        if (uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            var addr = Uri.UnescapeDataString(uri[7..].Split('?')[0]);
            ComposeWindow.ShowCompose(ComposeMode.New, null, addr);
            return;
        }
        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    private async void DownloadPictures_Click(object sender, RoutedEventArgs e)
    {
        _allowRemote = true;
        ImagesBar.Visibility = Visibility.Collapsed;
        if (Content is { } c && _init != null && await _init) await NavigateAsync(c);
    }

    public async Task PrintAsync()
    {
        if (Content == null || _init == null || !await _init) return;
        Browser.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
    }

    private void Reply_Click(object sender, RoutedEventArgs e) { if (Content is { } c) ComposeWindow.ShowCompose(ComposeMode.Reply, c.Header.Id); }
    private void ReplyAll_Click(object sender, RoutedEventArgs e) { if (Content is { } c) ComposeWindow.ShowCompose(ComposeMode.ReplyAll, c.Header.Id); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (Content is { } c) ComposeWindow.ShowCompose(ComposeMode.Forward, c.Header.Id); }

    private static string SafeName(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name;
    }

    private async void Attachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AttachmentInfo a || Content is not { } c) return;
        await OpenAttachmentAsync(c.Header.Id, a);
    }

    private static async Task OpenAttachmentAsync(long messageId, AttachmentInfo a)
    {
        var ext = Path.GetExtension(a.FileName).ToLowerInvariant();
        if (ext is ".exe" or ".bat" or ".cmd" or ".com" or ".scr" or ".vbs" or ".js" or ".msi" or ".ps1" or ".lnk" or ".hta" or ".jar" &&
            MessageBox.Show(Loc.F("Main.AttachmentHarmConfirm", a.FileName), "Imvello", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Imvello", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, SafeName(a.FileName));
            await AppServices.Messages.SaveAttachmentAsync(messageId, a.Index, path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Imvello", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static async Task SaveAttachmentAsync(long messageId, AttachmentInfo a)
    {
        var dlg = new SaveFileDialog { FileName = SafeName(a.FileName), Filter = Loc.T("Main.AllFilesFilter") };
        if (dlg.ShowDialog() != true) return;
        try { await AppServices.Messages.SaveAttachmentAsync(messageId, a.Index, dlg.FileName); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Imvello", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async Task SaveAllAsync(MessageContent c)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Main.SaveAllAttachments") };
        if (dlg.ShowDialog() != true) return;
        try
        {
            foreach (var a in c.Attachments)
            {
                var path = Path.Combine(dlg.FolderName, SafeName(a.FileName));
                var n = 1;
                while (File.Exists(path)) path = Path.Combine(dlg.FolderName, $"{Path.GetFileNameWithoutExtension(a.FileName)} ({n++}){Path.GetExtension(a.FileName)}");
                await AppServices.Messages.SaveAttachmentAsync(c.Header.Id, a.Index, path);
            }
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Imvello", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Attachment_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AttachmentInfo a || Content is not { } c) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        void Add(string header, Func<Task> action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += async (_, _) => await action();
            menu.Items.Add(mi);
        }
        Add(Loc.T("Main.Open"), () => OpenAttachmentAsync(c.Header.Id, a));
        Add(Loc.T("Main.SaveAsEllipsis"), () => SaveAttachmentAsync(c.Header.Id, a));
        if (c.Attachments.Count > 1) Add(Loc.T("Main.SaveAllAttachmentsEllipsis"), () => SaveAllAsync(c));
        menu.IsOpen = true;
    }
}
