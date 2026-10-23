using Dapper;
using Imvello.Models;

namespace Imvello.Data;

public sealed partial class MailDatabase
{
    private const string ContactCols = "id, name, email, phone, company, notes, use_count, last_used, created";

    public async Task<List<Contact>> GetContactsAsync(string? filter)
    {
        var sql = $"SELECT {ContactCols} FROM contacts";
        object? param = null;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            param = new { p = "%" + EscapeLike(filter.Trim()) + "%" };
            sql += " WHERE name LIKE @p ESCAPE '\\' OR email LIKE @p ESCAPE '\\' OR phone LIKE @p ESCAPE '\\' OR company LIKE @p ESCAPE '\\'";
        }
        sql += " ORDER BY CASE WHEN name='' THEN email ELSE name END COLLATE NOCASE";
        using var c = Open();
        var rows = await c.QueryAsync<Contact>(sql, param);
        return rows.ToList();
    }

    public async Task<List<Contact>> SuggestContactsAsync(string prefix, int limit)
    {
        var esc = EscapeLike(prefix.Trim());
        var contains = "%" + esc + "%";
        var starts = esc + "%";
        using var c = Open();
        var rows = await c.QueryAsync<Contact>("""
            SELECT id, name, email, phone, company, notes, use_count, last_used, created FROM contacts
            WHERE name LIKE @contains ESCAPE '\' OR email LIKE @contains ESCAPE '\'
            ORDER BY CASE WHEN name LIKE @starts ESCAPE '\' OR email LIKE @starts ESCAPE '\' THEN 0 ELSE 1 END,
                     use_count DESC, last_used DESC
            LIMIT @limit
            """, new { contains, starts, limit });
        return rows.ToList();
    }

    public async Task<long> SaveContactAsync(Contact c)
    {
        var email = (c.Email ?? "").Trim();
        var name = c.Name ?? "";
        var phone = c.Phone ?? "";
        var company = c.Company ?? "";
        var notes = c.Notes ?? "";
        using var cn = Open();
        if (c.Id == 0)
        {
            var now = DateTimeOffset.Now;
            var id = await cn.ExecuteScalarAsync<long?>("""
                INSERT INTO contacts(name,email,phone,company,notes,created)
                VALUES(@name,@email,@phone,@company,@notes,@now)
                ON CONFLICT(email) DO UPDATE SET name=excluded.name, phone=excluded.phone, company=excluded.company, notes=excluded.notes
                RETURNING id
                """, new { name, email, phone, company, notes, now });
            return id ?? 0;
        }
        await cn.ExecuteAsync("UPDATE contacts SET name=@name, email=@email, phone=@phone, company=@company, notes=@notes WHERE id=@id",
            new { name, email, phone, company, notes, c.Id });
        return c.Id;
    }

    public async Task DeleteContactAsync(long id)
    {
        using var c = Open();
        await c.ExecuteAsync("DELETE FROM contacts WHERE id=@id", new { id });
    }

    public async Task RememberRecipientsAsync(IEnumerable<(string Name, string Email)> recipients)
    {
        var list = recipients.Select(r => (Name: r.Name ?? "", Email: (r.Email ?? "").Trim())).Where(r => r.Email.Length > 0).ToList();
        if (list.Count == 0) return;
        using var c = Open();
        using var tx = c.BeginTransaction();
        var now = DateTimeOffset.Now;
        foreach (var r in list)
            await c.ExecuteAsync("""
                INSERT INTO contacts(name,email,use_count,last_used,created) VALUES(@name,@email,1,@now,@now)
                ON CONFLICT(email) DO UPDATE SET use_count=use_count+1, last_used=@now,
                    name=CASE WHEN contacts.name='' THEN excluded.name ELSE contacts.name END
                """, new { r.Name, r.Email, now }, tx);
        tx.Commit();
    }
}
