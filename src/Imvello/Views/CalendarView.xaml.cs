using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Imvello.Models;
using Imvello.Services;

namespace Imvello.Views;

public partial class CalendarView : UserControl
{
    private CalendarEvent? _selected;
    private bool _loading;
    private long? _pendingSelect;

    public CalendarView()
    {
        InitializeComponent();
        MonthCalendar.SelectedDate = DateTime.Today;
        MonthCalendar.DisplayDate = DateTime.Today;
    }

    private DateTime SelectedDay => (MonthCalendar.SelectedDate ?? DateTime.Today).Date;

    public void Activate() => _ = ReloadSafeAsync();

    private async Task ReloadSafeAsync(long? selectId = null)
    {
        try { await ReloadAsync(selectId); }
        catch { }
    }

    private async Task ReloadAsync(long? selectId)
    {
        var day = SelectedDay;
        DayHeading.Text = day.ToString("D", CultureInfo.CurrentCulture);
        var from = new DateTimeOffset(day);
        var to = new DateTimeOffset(day.AddDays(1));
        selectId ??= EventList.SelectedItem is ListBoxItem { Tag: CalendarEvent cur } ? cur.Id : null;
        var list = await Task.Run(() => AppServices.Db.GetEventsAsync(from, to));
        _loading = true;
        try
        {
            EventList.Items.Clear();
            foreach (var e in list) EventList.Items.Add(BuildItem(e));
            NoEventsText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CountText.Text = Loc.F("Calendar.Count", list.Count);
            if (selectId is { } sid)
                foreach (var item in EventList.Items.OfType<ListBoxItem>())
                    if (item.Tag is CalendarEvent ev && ev.Id == sid) { EventList.SelectedItem = item; break; }
        }
        finally { _loading = false; }
    }

    private static ListBoxItem BuildItem(CalendarEvent e)
    {
        var time = e.AllDay ? Loc.T("Calendar.AllDayLabel") : $"{e.Start:t} – {e.End:t}";
        var text = new StackPanel { Margin = new Thickness(10, 7, 10, 7) };
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(e.Title) ? Loc.T("Calendar.Untitled") : e.Title,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var sub = new TextBlock { Text = string.IsNullOrWhiteSpace(e.Location) ? time : $"{time} · {e.Location}", FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        text.Children.Add(sub);
        var bar = new Border { Width = 4, Margin = new Thickness(0, 4, 0, 4) };
        bar.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Left);
        dock.Children.Add(bar);
        dock.Children.Add(text);
        return new ListBoxItem { Content = dock, Tag = e };
    }

    private async void MonthCalendar_SelectedDatesChanged(object sender, SelectionChangedEventArgs e)
    {
        Mouse.Capture(null);
        ShowEmpty();
        var select = _pendingSelect;
        _pendingSelect = null;
        await ReloadSafeAsync(select);
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        MonthCalendar.DisplayDate = DateTime.Today;
        MonthCalendar.SelectedDate = DateTime.Today;
    }

    private void NewEvent_Click(object sender, RoutedEventArgs e)
    {
        EventList.SelectedItem = null;
        var start = SelectedDay.AddHours(DateTime.Now.Hour + 1);
        if (start.Date != SelectedDay) start = SelectedDay.AddHours(9);
        ShowEditor(new CalendarEvent { Start = start, End = start.AddHours(1) });
        TitleBox.Focus();
    }

    private void EventList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (EventList.SelectedItem is ListBoxItem { Tag: CalendarEvent ev }) ShowEditor(ev);
        else if (_selected is { Id: > 0 }) ShowEmpty();
    }

    private void ShowEmpty()
    {
        _selected = null;
        EditorPanel.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Visible;
    }

    private void ShowEditor(CalendarEvent ev)
    {
        _selected = ev;
        HintText.Visibility = Visibility.Collapsed;
        EditorPanel.Visibility = Visibility.Visible;
        EventHeading.Text = ev.Id == 0 ? Loc.T("Calendar.NewEvent") : (string.IsNullOrWhiteSpace(ev.Title) ? Loc.T("Calendar.Untitled") : ev.Title);
        TitleBox.Text = ev.Title;
        LocationBox.Text = ev.Location;
        NotesBox.Text = ev.Notes;
        DateBox.SelectedDate = ev.Start.LocalDateTime.Date;
        AllDayBox.IsChecked = ev.AllDay;
        StartBox.Text = ev.Start.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        EndBox.Text = ev.End.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        DeleteButton.Visibility = ev.Id > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTimeEnabled();
    }

    private void AllDayBox_Changed(object sender, RoutedEventArgs e) => UpdateTimeEnabled();

    private void UpdateTimeEnabled() => TimePanel.IsEnabled = AllDayBox.IsChecked != true;

    private static bool TryParseTime(string s, out TimeSpan t) =>
        TimeSpan.TryParseExact(s.Trim(), new[] { @"h\:mm", @"hh\:mm", "hhmm", "%h" }, CultureInfo.InvariantCulture, out t) && t < TimeSpan.FromDays(1);

    private void Warn(string key) =>
        MessageBox.Show(Window.GetWindow(this)!, Loc.T(key), "Imvello", MessageBoxButton.OK, MessageBoxImage.Warning);

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var title = TitleBox.Text.Trim();
        if (title.Length == 0) { Warn("Calendar.InvalidTitle"); TitleBox.Focus(); return; }
        var date = (DateBox.SelectedDate ?? SelectedDay).Date;
        var allDay = AllDayBox.IsChecked == true;
        DateTime start, end;
        if (allDay)
        {
            start = date;
            end = date.AddDays(1).AddSeconds(-1);
        }
        else
        {
            if (!TryParseTime(StartBox.Text, out var st) || !TryParseTime(EndBox.Text, out var et) || et <= st)
            {
                Warn("Calendar.InvalidTime");
                StartBox.Focus();
                return;
            }
            start = date + st;
            end = date + et;
        }

        var ev = new CalendarEvent
        {
            Id = _selected.Id,
            Title = title,
            Location = LocationBox.Text.Trim(),
            Notes = NotesBox.Text.Trim(),
            Start = new DateTimeOffset(start),
            End = new DateTimeOffset(end),
            AllDay = allDay,
            Color = _selected.Color
        };
        try
        {
            var id = await Task.Run(() => AppServices.Db.SaveEventAsync(ev));
            ev.Id = id;
            if (date != SelectedDay)
            {
                _pendingSelect = id;
                MonthCalendar.DisplayDate = date;
                MonthCalendar.SelectedDate = date;
            }
            else await ReloadSafeAsync(id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, ex.Message, "Imvello", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { Id: > 0 } ev) return;
        if (MessageBox.Show(Window.GetWindow(this)!, Loc.F("Calendar.DeleteConfirm", ev.Title), "Imvello", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await Task.Run(() => AppServices.Db.DeleteEventAsync(ev.Id));
            ShowEmpty();
            await ReloadSafeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, ex.Message, "Imvello", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
