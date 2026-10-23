namespace Imvello.Models;

public sealed class CalendarEvent
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Location { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public bool AllDay { get; set; }
    public string Color { get; set; } = "";
}
