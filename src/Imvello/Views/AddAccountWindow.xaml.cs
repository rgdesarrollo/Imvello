using System.Windows;
using Imvello.Models;
using Imvello.Services;

namespace Imvello.Views;

public partial class AddAccountWindow : Window
{
    private static readonly SecurityMode[] Modes = [SecurityMode.SslOnConnect, SecurityMode.StartTls, SecurityMode.Auto, SecurityMode.None];
    private AccountKind _kind;
    private CancellationTokenSource? _cts;

    public Account? Result { get; private set; }

    public AddAccountWindow()
    {
        InitializeComponent();
        var modeNames = new[] { Loc.T("Dialogs.SecuritySslTls"), Loc.T("Dialogs.SecurityStartTls"), Loc.T("Dialogs.SecurityAuto"), Loc.T("Dialogs.SecurityNone") };
        ImapSecBox.ItemsSource = modeNames;
        SmtpSecBox.ItemsSource = modeNames;
        Loaded += (_, _) => EmailBox.Focus();
    }

    public static Account? ShowFor(Window owner)
    {
        var w = new AddAccountWindow { Owner = owner };
        return w.ShowDialog() == true ? w.Result : null;
    }

    private void ShowPage(FrameworkElement page)
    {
        foreach (var p in new FrameworkElement[] { EmailPage, ProviderPage, ImapPage, BusyPanel })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorBar.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string NameFromEmail(string email)
    {
        var local = email.Split('@')[0].Replace('.', ' ').Replace('_', ' ');
        return string.Join(' ', local.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpper(w[0]) + w[1..]));
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        if (!email.Contains('@') || email.StartsWith('@') || email.EndsWith('@'))
        {
            ShowError(Loc.T("Dialogs.InvalidEmail"));
            return;
        }
        if (AppServices.Accounts.GetAccounts().Any(a => a.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError(Loc.T("Dialogs.AccountAlreadyAdded"));
            return;
        }
        ShowError(null);
        _kind = ManualCheck.IsChecked == true ? AccountKind.Imap : MailProviderDefaults.DetectKind(email);
        if (_kind == AccountKind.Imap) SetupImapPage(email);
        else SetupProviderPage(email);
    }

    private void SetupProviderPage(string email)
    {
        var google = _kind == AccountKind.Gmail;
        ProviderTitle.Text = google ? "Gmail" : "Outlook.com";
        ProviderEmail.Text = email;
        if (string.IsNullOrWhiteSpace(ProviderNameBox.Text)) ProviderNameBox.Text = NameFromEmail(email);
        OAuthText.Text = google ? Loc.T("Dialogs.SignInWithGoogle") : Loc.T("Dialogs.SignInWithMicrosoft");
        var configured = google ? OAuth2Service.IsGoogleConfigured(AppServices.Settings) : OAuth2Service.IsMicrosoftConfigured(AppServices.Settings);
        OAuthHint.Text = configured
            ? Loc.T("Dialogs.OAuthHintBrowser")
            : Loc.F("Dialogs.OAuthHintNeedClientId", google ? "Google" : "Microsoft") + ".";
        var simpleGmail = google && !configured;
        OAuthButton.Visibility = OAuthHint.Visibility = OrSeparator.Visibility = simpleGmail ? Visibility.Collapsed : Visibility.Visible;
        AppPasswordSteps.Visibility = google ? Visibility.Visible : Visibility.Collapsed;
        PasswordSection.Visibility = Visibility.Visible;
        PasswordLabel.Text = google ? Loc.T("Dialogs.AppPassword") : Loc.T("Dialogs.Password");
        PasswordHint.Text = google
            ? Loc.T("Dialogs.GooglePasswordHint")
            : Loc.T("Dialogs.MicrosoftPasswordHint");
        ShowPage(ProviderPage);
        ProviderPasswordBox.Focus();
    }

    private void CreateAppPassword_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://myaccount.google.com/apppasswords") { UseShellExecute = true }); }
        catch { }
        ProviderPasswordBox.Focus();
    }

    private void SetupImapPage(string email)
    {
        var a = new Account { Email = email, Kind = AccountKind.Imap };
        MailProviderDefaults.Apply(a);
        ImapEmailBox.Text = email;
        NameBox.Text = string.IsNullOrWhiteSpace(NameBox.Text) ? NameFromEmail(email) : NameBox.Text;
        UserBox.Text = a.UserName;
        ImapHostBox.Text = a.ImapHost;
        ImapPortBox.Text = a.ImapPort.ToString();
        ImapSecBox.SelectedIndex = Array.IndexOf(Modes, a.ImapSecurity);
        SmtpHostBox.Text = a.SmtpHost;
        SmtpPortBox.Text = a.SmtpPort.ToString();
        SmtpSecBox.SelectedIndex = Array.IndexOf(Modes, a.SmtpSecurity);
        ShowPage(ImapPage);
        PasswordBox.Focus();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        ShowError(null);
        ShowPage(EmailPage);
    }

    private void CancelBusy_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private Account? BuildImapAccount()
    {
        if (!int.TryParse(ImapPortBox.Text, out var ip) || !int.TryParse(SmtpPortBox.Text, out var sp) || ip <= 0 || sp <= 0)
        {
            ShowError(Loc.T("Dialogs.InvalidPortNumbers"));
            return null;
        }
        if (string.IsNullOrWhiteSpace(ImapHostBox.Text) || string.IsNullOrWhiteSpace(SmtpHostBox.Text))
        {
            ShowError(Loc.T("Dialogs.InvalidServerNames"));
            return null;
        }
        return new Account
        {
            Kind = AccountKind.Imap,
            DisplayName = NameBox.Text.Trim(),
            Email = ImapEmailBox.Text.Trim(),
            UserName = string.IsNullOrWhiteSpace(UserBox.Text) ? ImapEmailBox.Text.Trim() : UserBox.Text.Trim(),
            ImapHost = ImapHostBox.Text.Trim(),
            ImapPort = ip,
            ImapSecurity = Modes[Math.Max(0, ImapSecBox.SelectedIndex)],
            SmtpHost = SmtpHostBox.Text.Trim(),
            SmtpPort = sp,
            SmtpSecurity = Modes[Math.Max(0, SmtpSecBox.SelectedIndex)],
            AuthType = AuthType.Password
        };
    }

    private async Task<bool> RunBusyAsync(string text, FrameworkElement returnPage, Func<CancellationToken, Task> work)
    {
        ShowError(null);
        _cts = new CancellationTokenSource();
        BusyText.Text = text;
        ShowPage(BusyPanel);
        try
        {
            await work(_cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            ShowPage(returnPage);
            return false;
        }
        catch (Exception ex)
        {
            ShowPage(returnPage);
            ShowError(ex.Message);
            return false;
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (BuildImapAccount() is not { } a) return;
        var pwd = PasswordBox.Password;
        (bool ok, bool imapOk, bool smtpOk, string message) result = default;
        if (!await RunBusyAsync(Loc.T("Dialogs.TestingAccountSettings"), ImapPage, async ct => result = await AppServices.Accounts.TestAsync(a, pwd, ct))) return;
        ShowPage(ImapPage);
        if (result.ok) MessageBox.Show(this, Loc.T("Dialogs.TestsSucceeded"), Loc.T("Dialogs.TestAccountSettingsTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        else ShowError(result.message);
    }

    private async void ImapConnect_Click(object sender, RoutedEventArgs e)
    {
        if (BuildImapAccount() is not { } a) return;
        if (PasswordBox.Password.Length == 0) { ShowError(Loc.T("Dialogs.EnterPassword")); return; }
        await ConnectWithPasswordAsync(a, PasswordBox.Password, ImapPage);
    }

    private async void PasswordConnect_Click(object sender, RoutedEventArgs e)
    {
        if (ProviderPasswordBox.Password.Length == 0) { ShowError(Loc.T("Dialogs.EnterPasswordOrBrowser")); return; }
        var a = new Account { Kind = _kind, Email = EmailBox.Text.Trim(), DisplayName = ProviderNameBox.Text.Trim(), AuthType = AuthType.Password };
        MailProviderDefaults.Apply(a);
        await ConnectWithPasswordAsync(a, _kind == AccountKind.Gmail ? ProviderPasswordBox.Password.Replace(" ", "") : ProviderPasswordBox.Password, ProviderPage);
    }

    private async Task ConnectWithPasswordAsync(Account a, string secret, FrameworkElement page)
    {
        if (string.IsNullOrWhiteSpace(a.DisplayName)) a.DisplayName = NameFromEmail(a.Email);
        (bool ok, bool imapOk, bool smtpOk, string message) test = default;
        if (!await RunBusyAsync(Loc.F("Dialogs.ConnectingTo", a.ImapHost), page, async ct => test = await AppServices.Accounts.TestAsync(a, secret, ct))) return;
        if (!test.imapOk) { ShowPage(page); ShowError(test.message); return; }
        if (!test.smtpOk)
        {
            ShowPage(page);
            if (MessageBox.Show(this, Loc.F("Dialogs.SmtpFailedAddAnyway", test.message), Loc.T("Dialogs.TestAccountSettingsTitle"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }
        if (!await RunBusyAsync(Loc.T("Dialogs.SettingUpAccount"), page, async ct => Result = await AppServices.Accounts.AddAccountAsync(a, secret, null, ct))) return;
        DialogResult = true;
    }

    private async void OAuth_Click(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        OAuthTokens? tokens = null;
        if (!await RunBusyAsync(Loc.T("Dialogs.WaitingForBrowserSignIn"), ProviderPage, async ct =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                try { tokens = await AppServices.Accounts.SignInAsync(_kind, email, timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(Loc.T("Dialogs.OAuthSignInTimedOut"));
                }
            })) return;
        Activate();
        var a = new Account
        {
            Kind = _kind,
            Email = string.IsNullOrWhiteSpace(tokens!.Email) ? email : tokens.Email!,
            DisplayName = string.IsNullOrWhiteSpace(ProviderNameBox.Text) ? NameFromEmail(email) : ProviderNameBox.Text.Trim(),
            AuthType = AuthType.OAuth2
        };
        a.UserName = a.Email;
        MailProviderDefaults.Apply(a);
        if (!await RunBusyAsync(Loc.T("Dialogs.SettingUpAccount"), ProviderPage, async ct => Result = await AppServices.Accounts.AddAccountAsync(a, "", tokens, ct))) return;
        DialogResult = true;
    }
}
