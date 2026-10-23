using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Imvello.Models;
using Imvello.Services;
using MimeKit;

namespace Imvello.Views;

public partial class ContactsView : UserControl
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x0F, 0x6C, 0xBD), Color.FromRgb(0xC2, 0x39, 0xB3), Color.FromRgb(0x13, 0xA1, 0x0E), Color.FromRgb(0xCA, 0x50, 0x10),
        Color.FromRgb(0x86, 0x64, 0xB8), Color.FromRgb(0x03, 0x83, 0x87), Color.FromRgb(0xD1, 0x34, 0x38), Color.FromRgb(0x4F, 0x6B, 0xED)
    ];

    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private Contact? _selected;

    public ContactsView()
    {
        InitializeComponent();
        _searchTimer.Tick += async (_, _) => await ReloadSafeAsync();
    }

    public void Activate() => _ = ReloadSafeAsync();

    private async Task ReloadSafeAsync()
    {
        try { await ReloadAsync(); }
        catch { }
    }

    private async Task ReloadAsync()
    {
        var filter = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim();
        var selectedId = ContactList.SelectedItem is ListBoxItem { Tag: Contact cur } ? cur.Id : (long?)null;
        var list = await Task.Run(() => AppServices.Db.GetContactsAsync(filter));
        ContactList.Items.Clear();
        foreach (var c in list) ContactList.Items.Add(BuildItem(c));
        CountText.Text = Loc.F("Contacts.Count", list.Count);
        if (selectedId is { } sid)
            foreach (var item in ContactList.Items.OfType<ListBoxItem>())
                if (item.Tag is Contact c && c.Id == sid) { ContactList.SelectedItem = item; break; }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private static ListBoxItem BuildItem(Contact c)
    {
        var avatar = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Margin = new Thickness(10, 6, 8, 6),
            Background = AvatarBrush(c.Email),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = Initials(c.DisplayName),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var name = new TextBlock { Text = c.DisplayName, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var email = new TextBlock { Text = c.Email, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        email.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        var info = new StackPanel { Margin = new Thickness(0, 7, 8, 7) };
        info.Children.Add(name);
        info.Children.Add(email);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(avatar);
        row.Children.Add(info);
        return new ListBoxItem { Content = row, Tag = c, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    private void ContactList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContactList.SelectedItem is ListBoxItem { Tag: Contact c }) ShowDetails(c);
        else ShowEmpty();
    }

    private void ShowDetails(Contact c)
    {
        _selected = c;
        NameBox.Text = c.Name;
        EmailBox.Text = c.Email;
        PhoneBox.Text = c.Phone;
        CompanyBox.Text = c.Company;
        NotesBox.Text = c.Notes;
        BigAvatar.Background = AvatarBrush(c.Email);
        BigInitials.Text = Initials(c.DisplayName);
        NameHeading.Text = c.DisplayName;
        StatsText.Text = c.UseCount > 0
            ? Loc.F("Contacts.TimesEmailed", c.UseCount) + "  " + Loc.F("Contacts.LastEmailed", c.LastUsed.ToLocalTime().ToString("MMM d, yyyy h:mm tt"))
            : "";
        DeleteButton.IsEnabled = c.Id > 0;
        SendButton.IsEnabled = c.Email.Length > 0;
        EditorPanel.Visibility = Visibility.Visible;
        HintText.Visibility = Visibility.Collapsed;
    }

    private void ShowEmpty()
    {
        _selected = null;
        EditorPanel.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Visible;
    }

    private void NewContact_Click(object sender, RoutedEventArgs e)
    {
        ContactList.SelectedItem = null;
        _selected = new Contact();
        NameBox.Text = "";
        EmailBox.Text = "";
        PhoneBox.Text = "";
        CompanyBox.Text = "";
        NotesBox.Text = "";
        BigAvatar.Background = AvatarBrush("");
        BigInitials.Text = "?";
        NameHeading.Text = Loc.T("Contacts.NewContact");
        StatsText.Text = "";
        DeleteButton.IsEnabled = false;
        SendButton.IsEnabled = false;
        EditorPanel.Visibility = Visibility.Visible;
        HintText.Visibility = Visibility.Collapsed;
        NameBox.Focus();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        if (!email.Contains('@') || !MailboxAddress.TryParse(email, out _))
        {
            MessageBox.Show(Window.GetWindow(this)!, Loc.T("Contacts.InvalidEmail"), "Imvello", MessageBoxButton.OK, MessageBoxImage.Warning);
            EmailBox.Focus();
            return;
        }
        var c = _selected ?? new Contact();
        c.Name = NameBox.Text.Trim();
        c.Email = email;
        c.Phone = PhoneBox.Text.Trim();
        c.Company = CompanyBox.Text.Trim();
        c.Notes = NotesBox.Text.Trim();
        try
        {
            var id = await Task.Run(() => AppServices.Db.SaveContactAsync(c));
            c.Id = id;
            await ReloadSafeAsync();
            foreach (var item in ContactList.Items.OfType<ListBoxItem>())
                if (item.Tag is Contact cc && cc.Id == id) { ContactList.SelectedItem = item; break; }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, ex.Message, "Imvello", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { Email.Length: > 0 } c) ComposeWindow.ShowCompose(ComposeMode.New, null, c.AddressText);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { Id: > 0 } c) return;
        if (MessageBox.Show(Window.GetWindow(this)!, Loc.F("Contacts.DeleteConfirm", c.DisplayName), "Imvello", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await Task.Run(() => AppServices.Db.DeleteContactAsync(c.Id));
            await ReloadSafeAsync();
            ShowEmpty();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, ex.Message, "Imvello", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ContactList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<ListBoxItem>(d) is { Tag: Contact c } && c.Id > 0)
            ComposeWindow.ShowCompose(ComposeMode.New, null, c.AddressText);
    }

    private static string Initials(string s)
    {
        var parts = s.Split([' ', '.', '_', '@'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1 || s.Contains('@')) return parts[0][..1].ToUpperInvariant();
        return (parts[0][..1] + parts[^1][..1]).ToUpperInvariant();
    }

    private static Brush AvatarBrush(string email)
    {
        uint hash = 17;
        foreach (var ch in email.ToLowerInvariant()) hash = unchecked(hash * 31 + ch);
        var b = new SolidColorBrush(Palette[(int)(hash % (uint)Palette.Length)]);
        b.Freeze();
        return b;
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}
