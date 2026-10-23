using Dapper;
using Imvello.Models;
using Microsoft.Data.Sqlite;

namespace Imvello.Data;

public sealed partial class MailDatabase
{
    private const string HeaderCols = "m.id, m.folder_id, m.account_id, m.uid, m.message_id, m.in_reply_to, m.refs AS \"References\", m.subject, m.from_name, m.from_address, m.to_list AS \"To\", m.cc_list AS \"Cc\", m.date, m.size, m.flags, m.has_attachments, m.preview, m.importance, m.body_downloaded";

    public async Task UpsertHeadersAsync(IList<MessageHeader> headers)
    {
        if (headers.Count == 0) return;
        await using var c = Open();
        await using var tx = c.BeginTransaction();

        var find = c.CreateCommand();
        find.Transaction = tx;
        find.CommandText = "SELECT id FROM messages WHERE folder_id=$f AND uid=$u";
        var fF = find.Parameters.Add("$f", SqliteType.Integer);
        var fU = find.Parameters.Add("$u", SqliteType.Integer);

        var upd = c.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE messages SET flags=$fl WHERE id=$id";
        var uFl = upd.Parameters.Add("$fl", SqliteType.Integer);
        var uId = upd.Parameters.Add("$id", SqliteType.Integer);

        var ins = c.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO messages(folder_id, account_id, uid, message_id, in_reply_to, refs, subject, from_name, from_address, to_list, cc_list, date, size, flags, has_attachments, preview, importance, body_downloaded)
            VALUES($folder, $account, $uid, $mid, $irt, $refs, $subject, $fname, $faddr, $to, $cc, $date, $size, $flags, $att, $preview, $imp, $body) RETURNING id
            """;
        string[] names = ["$folder", "$account", "$uid", "$mid", "$irt", "$refs", "$subject", "$fname", "$faddr", "$to", "$cc", "$date", "$size", "$flags", "$att", "$preview", "$imp", "$body"];
        var p = names.Select(n => ins.Parameters.Add(n, SqliteType.Text)).ToArray();

        var fts = c.CreateCommand();
        fts.Transaction = tx;
        fts.CommandText = "INSERT INTO messages_fts(rowid, subject, from_text, to_text, body_text) VALUES($id, $s, $f, $t, $b)";
        var tId = fts.Parameters.Add("$id", SqliteType.Integer);
        var tS = fts.Parameters.Add("$s", SqliteType.Text);
        var tF = fts.Parameters.Add("$f", SqliteType.Text);
        var tT = fts.Parameters.Add("$t", SqliteType.Text);
        var tB = fts.Parameters.Add("$b", SqliteType.Text);

        foreach (var h in headers)
        {
            fF.Value = h.FolderId;
            fU.Value = h.Uid;
            var existing = await find.ExecuteScalarAsync();
            if (existing is long id)
            {
                h.Id = id;
                uFl.Value = (int)h.Flags;
                uId.Value = id;
                await upd.ExecuteNonQueryAsync();
                continue;
            }
            object?[] vals = [h.FolderId, h.AccountId, h.Uid, h.MessageId, h.InReplyTo, h.References, h.Subject, h.FromName, h.FromAddress,
                h.To, h.Cc, h.Date.ToUnixTimeSeconds(), h.Size, (int)h.Flags, h.HasAttachments ? 1 : 0, h.Preview, h.Importance, h.BodyDownloaded ? 1 : 0];
            for (int i = 0; i < vals.Length; i++) p[i].Value = vals[i] ?? DBNull.Value;
            h.Id = (long)(await ins.ExecuteScalarAsync())!;
            tId.Value = h.Id;
            tS.Value = h.Subject ?? "";
            tF.Value = $"{h.FromName} {h.FromAddress}";
            tT.Value = $"{h.To} {h.Cc}";
            tB.Value = h.Preview ?? "";
            await fts.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task<long> InsertLocalMessageAsync(MessageHeader h)
    {
        await UpsertHeadersAsync([h]);
        return h.Id;
    }

    private static string Filter(bool unreadOnly, bool flaggedOnly) =>
        (unreadOnly ? " AND (m.flags & 1)=0" : "") + (flaggedOnly ? " AND (m.flags & 4)<>0" : "");

    public async Task<List<MessageHeader>> GetHeadersPageAsync(long folderId, int offset, int limit, string sortBy, bool desc, bool unreadOnly, bool flaggedOnly)
    {
        var col = sortBy switch
        {
            "From" => "m.from_name COLLATE NOCASE",
            "Subject" => "m.subject COLLATE NOCASE",
            "Size" => "m.size",
            _ => "m.date"
        };
        var dir = desc ? "DESC" : "ASC";
        using var c = Open();
        var rows = await c.QueryAsync<MessageHeader>(
            $"SELECT {HeaderCols} FROM messages m WHERE m.folder_id=@folderId{Filter(unreadOnly, flaggedOnly)} ORDER BY {col} {dir}, m.id {dir} LIMIT @limit OFFSET @offset",
            new { folderId, limit, offset });
        return rows.ToList();
    }

    public async Task<int> CountAsync(long folderId, bool unreadOnly, bool flaggedOnly)
    {
        using var c = Open();
        return await c.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM messages m WHERE m.folder_id=@folderId{Filter(unreadOnly, flaggedOnly)}", new { folderId });
    }

    public async Task<MessageHeader?> GetMessageAsync(long id)
    {
        using var c = Open();
        return await c.QuerySingleOrDefaultAsync<MessageHeader>($"SELECT {HeaderCols} FROM messages m WHERE m.id=@id", new { id });
    }

    public async Task<List<MessageHeader>> GetMessagesAsync(IEnumerable<long> ids)
    {
        var list = ids.ToList();
        var result = new List<MessageHeader>();
        using var c = Open();
        foreach (var chunk in list.Chunk(500))
            result.AddRange(await c.QueryAsync<MessageHeader>($"SELECT {HeaderCols} FROM messages m WHERE m.id IN @chunk", new { chunk }));
        return result;
    }

    public async Task<Dictionary<long, long>> GetUidMapAsync(long folderId)
    {
        using var c = Open();
        var rows = await c.QueryAsync<(long uid, long id)>("SELECT uid, id FROM messages WHERE folder_id=@folderId", new { folderId });
        return rows.ToDictionary(r => r.uid, r => r.id);
    }

    public async Task<long> GetMaxUidAsync(long folderId)
    {
        using var c = Open();
        return await c.ExecuteScalarAsync<long?>("SELECT MAX(uid) FROM messages WHERE folder_id=@folderId", new { folderId }) ?? 0;
    }
}
