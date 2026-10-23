using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Imvello.Models;
using Imvello.Services;

namespace Imvello.Views;

public sealed class RecipientAutoComplete
{
    private readonly TextBox _box;
    private readonly Popup _popup;
    private readonly ListBox _list;
    private readonly DispatcherTimer _timer;
    private int _version;
    private bool _suppress;

    private RecipientAutoComplete(TextBox box)
    {
        _box = box;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += async (_, _) => await QueryAsync();
        _list = new ListBox
        {
            MaxHeight = 260,
            MinWidth = box.ActualWidth,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.PreviewMouseLeftButtonUp += OnListMouseUp;
        var border = new Border { BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "ControlBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        border.Child = _list;
        _popup = new Popup
        {
            PlacementTarget = box,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = border
        };
        box.TextChanged += (_, _) => OnTextChanged();
        box.PreviewKeyDown += OnPreviewKeyDown;
        box.LostKeyboardFocus += OnBoxLostKeyboardFocus;
    }

    public static RecipientAutoComplete Attach(TextBox box) => new(box);

    private void OnTextChanged()
    {
        _version++;
        _timer.Stop();
        if (CurrentToken().Length < 1) Close();
        else _timer.Start();
    }

    private string CurrentToken()
    {
        var text = _box.Text;
        var caret = Math.Clamp(_box.CaretIndex, 0, text.Length);
        var start = caret;
        while (start > 0 && text[start - 1] != ';' && text[start - 1] != ',') start--;
        return text[start..caret].Trim();
    }

    private async Task QueryAsync()
    {
        var token = CurrentToken();
        if (token.Length < 1) { Close(); return; }
        var version = _version;
        List<Contact>? results;
        try
        {
            results = await Task.Run(() => AppServices.Db.SuggestContactsAsync(token, 8));
        }
        catch
        {
            results = null;
        }
        if (version != _version || results == null) return;
        _list.Items.Clear();
        foreach (var c in results) _list.Items.Add(CreateItem(c));
        if (_list.Items.Count > 0)
        {
            _list.SelectedIndex = 0;
            _list.MinWidth = Math.Max(_box.ActualWidth, 100);
            _popup.IsOpen = true;
        }
        else Close();
    }

    private static ListBoxItem CreateItem(Contact c)
    {
        var name = new TextBlock { Text = c.DisplayName, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var email = new TextBlock { Text = c.Email, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        email.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        var panel = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
        panel.Children.Add(name);
        panel.Children.Add(email);
        return new ListBoxItem { Content = panel, Tag = c, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_popup.IsOpen || _list.Items.Count == 0) return;
        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                if (_list.SelectedIndex < _list.Items.Count - 1) { _list.SelectedIndex++; _list.ScrollIntoView(_list.SelectedItem); }
                break;
            case Key.Up:
                e.Handled = true;
                if (_list.SelectedIndex > 0) { _list.SelectedIndex--; _list.ScrollIntoView(_list.SelectedItem); }
                break;
            case Key.Enter:
            case Key.Tab:
                e.Handled = true;
                if (_list.SelectedItem is ListBoxItem { Tag: Contact c }) Accept(c);
                else Close();
                break;
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
        }
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<ListBoxItem>(d) is { Tag: Contact c })
        {
            e.Handled = true;
            Accept(c);
        }
    }

    private void OnBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_popup.IsOpen) return;
        _box.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_popup.IsOpen && !_popup.IsKeyboardFocusWithin) Close();
        }), DispatcherPriority.Input);
    }

    private void Accept(Contact c)
    {
        var text = _box.Text;
        var caret = Math.Clamp(_box.CaretIndex, 0, text.Length);
        var start = caret;
        while (start > 0 && text[start - 1] != ';' && text[start - 1] != ',') start--;
        var prefix = text[..start].TrimEnd();
        if (prefix.Length > 0)
        {
            var trimmed = prefix.TrimEnd(',', ';', ' ');
            prefix = trimmed.Length > 0 ? trimmed + "; " : "";
        }
        _suppress = true;
        try { _box.Text = prefix + c.AddressText + "; "; }
        finally { _suppress = false; }
        _box.CaretIndex = _box.Text.Length;
        _box.Focus();
        Close();
    }

    private void Close()
    {
        _timer.Stop();
        _popup.IsOpen = false;
        _list.Items.Clear();
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}
