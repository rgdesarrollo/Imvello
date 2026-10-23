using Dapper;
using Imvello.Models;

namespace Imvello.Data;

public sealed partial class MailDatabase
{
    private const string EventCols = "id, title, location, notes, start, \"end\" AS \"end\", all_day, color";

    public async Task<List<CalendarEvent>> GetEventsAsync(DateTimeOffset from, DateTimeOffset to)
    {
        using var c = Open();
        var rows = await c.QueryAsync<CalendarEvent>(
            $"SELECT {EventCols} FROM events WHERE start < @to AND \"end\" >= @from ORDER BY all_day DESC, start, title COLLATE NOCASE",
            new { from, to });
        return rows.ToList();
    }

    public async Task<long> SaveEventAsync(CalendarEvent e)
    {
        var p = new { e.Id, Title = e.Title ?? "", Location = e.Location ?? "", Notes = e.Notes ?? "", e.Start, e.End, e.AllDay, Color = e.Color ?? "" };
        using var c = Open();
        if (e.Id == 0)
            return await c.ExecuteScalarAsync<long>("""
                INSERT INTO events(title,location,notes,start,"end",all_day,color)
                VALUES(@Title,@Location,@Notes,@Start,@End,@AllDay,@Color) RETURNING id
                """, p);
        await c.ExecuteAsync("""
            UPDATE events SET title=@Title, location=@Location, notes=@Notes, start=@Start, "end"=@End, all_day=@AllDay, color=@Color
            WHERE id=@Id
            """, p);
        return e.Id;
    }

    public async Task DeleteEventAsync(long id)
    {
        using var c = Open();
        await c.ExecuteAsync("DELETE FROM events WHERE id=@id", new { id });
    }
}
