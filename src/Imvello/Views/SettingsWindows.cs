using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Imvello.Models;
using Imvello.Services;

namespace Imvello.Views;

internal sealed class FormBuilder
{
    public Grid Grid { get; } = new();

    public FormBuilder(double labelWidth = 150)
    {
        Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        Grid.ColumnDefinitions.Add(new ColumnDefinition());
    }

    private int NewRow()
    {
        Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return Grid.RowDefinitions.Count - 1;
    }

    public T Add<T>(string label, T control) where T : FrameworkElement
    {
        var r = NewRow();
        var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };
        control.Margin = new Thickness(0, 4, 0, 4);
        Grid.SetRow(l, r); Grid.SetRow(control, r); Grid.SetColumn(control, 1);
        Grid.Children.Add(l); Grid.Children.Add(control);
        return control;
    }

    public T Full<T>(T control) where T : FrameworkElement
    {
        var r = NewRow();
        if (control.Margin == default) control.Margin = new Thickness(0, 4, 0, 4);
        Grid.SetRow(control, r); Grid.SetColumnSpan(control, 2);
        Grid.Children.Add(control);
        return control;
    }

    public void Header(string text) => Full(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 14, 0, 4) });

    public void Hint(string text)
    {
        var hint = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        Full(hint);
    }

    public static TextBox Text(string value) => new() { Text = value, Style = (Style)Application.Current.FindResource("FieldBox"), Height = 28 };
}

public static class AccountSettingsWindow
{
    private static readonly SecurityMode[] Modes = [SecurityMode.SslOnConnect, SecurityMode.StartTls, SecurityMode.Auto, SecurityMode.None];

    public static bool Show(Window owner, Account account)
    {
        var a = AppServices.Db.GetAccount(account.Id) ?? account;
        var w = DialogHelper.Create(owner, Loc.F("Dialogs.AccountSettingsTitle", a.Email), 560);
        var f = new FormBuilder();
        f.Header(Loc.T("Dialogs.UserInformation"));
        var name = f.Add(Loc.T("Dialogs.YourName"), FormBuilder.Text(a.DisplayName));
        var email = f.Add(Loc.T("Dialogs.EmailAddress"), FormBuilder.Text(a.Email));
        email.IsReadOnly = true;
        email.SetResourceReference(Control.BackgroundProperty, "ReadOnlyBrush");
        var editable = a.Kind == AccountKind.Imap;

        f.Header(Loc.T("Dialogs.LogonInformation"));
        var user = f.Add(Loc.T("Dialogs.UserName"), FormBuilder.Text(a.UserName));
        user.IsEnabled = a.AuthType == AuthType.Password;
        var pwdBox = new PasswordBox { Height = 28, Padding = new Thickness(4, 5, 4, 4) };
        pwdBox.SetResourceReference(Control.BorderBrushProperty, "ControlBorderBrush");
        var pwd = f.Add(Loc.T("Dialogs.Password"), pwdBox);
        pwd.IsEnabled = a.AuthType == AuthType.Password;
        f.Hint(a.AuthType == AuthType.OAuth2 ? Loc.T("Dialogs.OAuthAccountHint") : Loc.T("Dialogs.PasswordKeepHint"));
        if (AccountService.CanReconnect(a) && owner is MainWindow main)
        {
            var provider = a.Kind == AccountKind.Outlook ? "Microsoft" : "Google";
            var reconnect = f.Full(new Button
            {
                Content = Loc.F("Main.ReconnectWith", provider),
                Style = (Style)Application.Current.FindResource("SecondaryButton"),
                HorizontalAlignment = HorizontalAlignment.Left
            });
            reconnect.Click += (_, _) => { w.DialogResult = false; main.ReconnectAccount(a); };
        }

        f.Header(Loc.T("Dialogs.ServerInformation"));
        TextBox imapHost = FormBuilder.Text(a.ImapHost), imapPort = FormBuilder.Text(a.ImapPort.ToString()), smtpHost = FormBuilder.Text(a.SmtpHost), smtpPort = FormBuilder.Text(a.SmtpPort.ToString());
        var modeNames = new[] { Loc.T("Dialogs.SecuritySslTls"), Loc.T("Dialogs.SecurityStartTls"), Loc.T("Dialogs.SecurityAuto"), Loc.T("Dialogs.SecurityNone") };
        ComboBox imapSec = new() { ItemsSource = modeNames, SelectedIndex = Array.IndexOf(Modes, a.ImapSecurity), Height = 28 };
        ComboBox smtpSec = new() { ItemsSource = modeNames, SelectedIndex = Array.IndexOf(Modes, a.SmtpSecurity), Height = 28 };
        Grid Row(TextBox host, TextBox port, ComboBox sec)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            port.Margin = new Thickness(6, 0, 0, 0); sec.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(port, 1); Grid.SetColumn(sec, 2);
            g.Children.Add(host); g.Children.Add(port); g.Children.Add(sec);
            g.IsEnabled = editable;
            return g;
        }
        f.Add(Loc.T("Dialogs.IncomingImap"), Row(imapHost, imapPort, imapSec));
        f.Add(Loc.T("Dialogs.OutgoingSmtp"), Row(smtpHost, smtpPort, smtpSec));

        f.Header(Loc.T("Dialogs.Signature"));
        var sig = f.Full(new TextBox
        {
            Text = HtmlUtil.HtmlToText(a.Signature ?? ""),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 90,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Style = (Style)Application.Current.FindResource("FieldBox"),
            VerticalContentAlignment = VerticalAlignment.Top
        });
        var sync = f.Full(new CheckBox { Content = Loc.T("Dialogs.IncludeInSendReceive"), IsChecked = a.Enabled });

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "ErrorTextBrush");
        f.Full(status);

        bool Save()
        {
            if (!int.TryParse(imapPort.Text, out var ip) || !int.TryParse(smtpPort.Text, out var sp)) { status.Text = Loc.T("Dialogs.InvalidPortNumbers"); return false; }
            a.DisplayName = name.Text.Trim();
            a.UserName = user.Text.Trim();
            if (editable)
            {
                a.ImapHost = imapHost.Text.Trim(); a.ImapPort = ip; a.ImapSecurity = Modes[Math.Max(0, imapSec.SelectedIndex)];
                a.SmtpHost = smtpHost.Text.Trim(); a.SmtpPort = sp; a.SmtpSecurity = Modes[Math.Max(0, smtpSec.SelectedIndex)];
            }
            a.Signature = string.IsNullOrWhiteSpace(sig.Text) ? null : sig.Text.TrimStart().StartsWith('<') ? sig.Text : HtmlUtil.TextToHtml(sig.Text);
            a.Enabled = sync.IsChecked == true;
            var newPwd = pwd.Password;
            try
            {
                AppServices.Accounts.UpdateAccountAsync(a, string.IsNullOrEmpty(newPwd) ? null : newPwd).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception ex) { status.Text = ex.Message; return false; }
        }

        w.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = SystemParameters.WorkArea.Height - 80,
            Content = new StackPanel { Margin = new Thickness(20, 6, 20, 18), Children = { f.Grid, DialogHelper.Buttons(w, Loc.T("Common.Save"), Save) } }
        };
        return w.ShowDialog() == true;
    }
}

public static class OptionsWindow
{
    public static bool Show(Window owner)
    {
        var s = AppServices.Settings;
        var w = DialogHelper.Create(owner, Loc.T("Dialogs.ImvelloOptionsTitle"), 600);
        var f = new FormBuilder(190);

        f.Header(Loc.T("Common.LanguageHeader"));
        var langCodes = new[] { "System", "en", "es" };
        var lang = f.Add(Loc.T("Common.LanguageLabel"), new ComboBox
        {
            ItemsSource = new[] { Loc.T("Common.LanguageSystem"), "English", "Español" },
            SelectedIndex = Math.Max(0, Array.IndexOf(langCodes, s.Language)),
            Height = 28, Width = 220, HorizontalAlignment = HorizontalAlignment.Left
        });

        f.Header(Loc.T("Dialogs.OptionsMail"));
        var interval = f.Add(Loc.T("Dialogs.CheckNewMailEvery"), new ComboBox { ItemsSource = new[] { 1, 2, 5, 10, 15, 30, 60 }, SelectedItem = s.SyncIntervalMinutes, Height = 28, Width = 90, HorizontalAlignment = HorizontalAlignment.Left });
        if (interval.SelectedItem == null) interval.SelectedItem = 5;
        var markRead = f.Full(new CheckBox { Content = Loc.T("Dialogs.MarkReadWhenViewed"), IsChecked = s.MarkAsReadWhenViewed });
        var full = f.Full(new CheckBox { Content = Loc.T("Dialogs.DownloadFullMessages"), IsChecked = s.DownloadFullMessages });
        var block = f.Full(new CheckBox { Content = Loc.T("Dialogs.BlockRemoteImages"), IsChecked = s.BlockRemoteImages });
        var confirm = f.Full(new CheckBox { Content = Loc.T("Dialogs.ConfirmDelete"), IsChecked = s.ConfirmDelete });
        var preview = f.Full(new CheckBox { Content = Loc.T("Dialogs.ShowPreview"), IsChecked = s.ShowPreview });

        f.Header(Loc.T("Dialogs.OAuthHeader"));
        f.Hint(Loc.T("Dialogs.OAuthHintParagraph"));
        var gid = f.Add(Loc.T("Dialogs.GoogleClientId"), FormBuilder.Text(s.GoogleClientId ?? ""));
        var gsecret = f.Add(Loc.T("Dialogs.GoogleClientSecret"), FormBuilder.Text(s.GoogleClientSecret ?? ""));
        var mid = f.Add(Loc.T("Dialogs.MicrosoftClientId"), FormBuilder.Text(s.MicrosoftClientId ?? ""));

        f.Header(Loc.T("Dialogs.DataHeader"));
        var data = f.Add(Loc.T("Dialogs.DataFolder"), FormBuilder.Text(s.DataDirectory));
        data.IsReadOnly = true;
        data.SetResourceReference(Control.BackgroundProperty, "ReadOnlyBrush");
        var open = f.Full(new Button { Content = Loc.T("Dialogs.OpenDataFolder"), Style = (Style)Application.Current.FindResource("SecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left });
        open.Click += (_, _) => { try { System.Diagnostics.Process.Start("explorer.exe", s.DataDirectory); } catch { } };

        bool Save()
        {
            string? googleId = null, googleSecret = string.IsNullOrWhiteSpace(gsecret.Text) ? null : gsecret.Text.Trim();
            if (!string.IsNullOrWhiteSpace(gid.Text))
            {
                if (!OAuth2Service.TryParseGoogleClient(gid.Text, gsecret.Text, out var parsedId, out googleSecret))
                {
                    MessageBox.Show(w, Loc.T("Dialogs.GoogleClientIdInvalid"), "Imvello", MessageBoxButton.OK, MessageBoxImage.Warning);
                    gid.Focus();
                    gid.SelectAll();
                    return false;
                }
                googleId = parsedId;
                gid.Text = parsedId;
                gsecret.Text = googleSecret ?? "";
            }
            string? microsoftId = null;
            if (!string.IsNullOrWhiteSpace(mid.Text))
            {
                if (!OAuth2Service.TryParseMicrosoftClient(mid.Text, out var parsedMid))
                {
                    MessageBox.Show(w, Loc.T("Dialogs.MicrosoftClientIdInvalid"), "Imvello", MessageBoxButton.OK, MessageBoxImage.Warning);
                    mid.Focus();
                    mid.SelectAll();
                    return false;
                }
                microsoftId = parsedMid;
                mid.Text = parsedMid;
            }
            s.SyncIntervalMinutes = interval.SelectedItem is int i ? i : 5;
            s.MarkAsReadWhenViewed = markRead.IsChecked == true;
            s.DownloadFullMessages = full.IsChecked == true;
            s.BlockRemoteImages = block.IsChecked == true;
            s.ConfirmDelete = confirm.IsChecked == true;
            s.ShowPreview = preview.IsChecked == true;
            s.GoogleClientId = googleId;
            s.GoogleClientSecret = googleSecret;
            s.MicrosoftClientId = microsoftId;
            s.Language = langCodes[Math.Max(0, lang.SelectedIndex)];
            s.Save();
            Loc.Apply(Application.Current, s.Language);
            return true;
        }

        w.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = SystemParameters.WorkArea.Height - 80,
            Content = new StackPanel { Margin = new Thickness(20, 6, 20, 18), Children = { f.Grid, DialogHelper.Buttons(w, Loc.T("Common.OK"), Save) } }
        };
        return w.ShowDialog() == true;
    }
}
